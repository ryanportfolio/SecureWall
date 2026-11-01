using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace pylorak.TinyWall.Prompting
{
    // Service control as the in-place upgrade steps see it. The Windows adapter wraps
    // SCM; tests supply a fake.
    internal interface IUpgradeService
    {
        bool Registered { get; }
        LifecycleServiceState State { get; }
        void Stop();
        void Start();
        void SetRestartOnFailure(bool enabled);
    }

    // File access for the policy snapshot. The adapter writes every snapshot file and
    // a restored secret with the protected secret DACL, and flushes each write.
    internal interface IUpgradeFiles
    {
        bool FileExists(string path);
        bool DirectoryExists(string path);
        byte[] ReadFile(string path);
        void WriteFile(string path, byte[] contents, bool secret);
        void DeleteFile(string path);
        void CreateDirectory(string path);
        void DeleteDirectory(string path);
        string[] Directories(string path);
    }

    // Decisions for the MSI in-place upgrade. Each side of the swap is run by the
    // version that knows it: the old package's /msi-upgrade-stop and its rollback
    // twin /msi-upgrade-restore, the new package's /msi-upgrade-start, its rollback
    // twin /msi-upgrade-abort and its commit step /msi-upgrade-commit. Every step
    // takes the transaction ID, the new package's ProductCode. No SCM, registry or
    // file system access lives here.
    internal static class UpgradeLifecyclePolicy
    {
        internal const string StopMode = "/msi-upgrade-stop";
        internal const string RestoreMode = "/msi-upgrade-restore";
        internal const string StartMode = "/msi-upgrade-start";
        internal const string AbortMode = "/msi-upgrade-abort";
        internal const string CommitMode = "/msi-upgrade-commit";
        internal static readonly string[] Modes = { StopMode, RestoreMode, StartMode, AbortMode, CommitMode };

        // REG_SZ under ProductRegistryPolicy.KeyPath, written by /msi-upgrade-stop
        // before it stops anything. Holds the transaction ID.
        internal const string MarkerValue = "UpgradeInProgress";
        internal const string SnapshotFolder = "upgrade";
        internal const string SnapshotManifest = "snapshot.complete";

        // Every machine-data file the service writes as policy: configuration, its
        // recovery journal, the password hash and the original hosts backup.
        // profiles.json and hosts.bck are shipped data and follow the binaries
        // through InstallDefaults.
        internal static readonly string[] SnapshotNames = { "config", "config.recovery", "pwd", "hosts.orig" };
        internal static readonly string[] SecretNames = { "pwd" };

        internal const string InProgressMessage =
            "SecureWall is being upgraded. Setup starts the SecureWall service again when it finishes; connections to other computers stay blocked until then.";

        // The MSI passes [ProductCode] or [UPGRADINGPRODUCTCODE]: a braced GUID.
        // Exactly one mode argument followed by exactly that GUID is accepted.
        internal static string Transaction(string[] args, string mode)
        {
            int index = -1;
            for (int i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], mode, StringComparison.Ordinal)) continue;
                if (index >= 0) throw new ArgumentException(mode + " was given more than once.");
                index = i;
            }
            if (index < 0) throw new ArgumentException(mode + " is missing.");
            if (args.Length != 2 || index != 0)
                throw new ArgumentException(mode + " takes exactly one transaction ID and no other argument.");
            string value = args[1];
            if (value.Length != 38 || !Guid.TryParseExact(value, "B", out Guid id))
                throw new ArgumentException(mode + " needs the transaction ID as a braced GUID.");
            return Canonical(id);
        }

        internal static string Canonical(Guid id) => id.ToString("B").ToUpperInvariant();

        internal static bool OwnsMarker(string? marker, string transaction) =>
            marker != null && string.Equals(marker.Trim(), transaction, StringComparison.OrdinalIgnoreCase);

        // Controller repair and /install outside the upgrade modes. A marker refuses a
        // start only while Windows Installer runs an installation, so a marker left by
        // a power loss never blocks SCM's boot start, a manual sc start or a later
        // repair. The upgrade's own starter passes its transaction ID.
        internal static bool BlocksStart(string? marker, bool installerRunning, string? transaction = null) =>
            !string.IsNullOrWhiteSpace(marker) && installerRunning &&
            (transaction == null || !OwnsMarker(marker, transaction));

        internal static string SnapshotRoot(string dataDirectory) => Path.Combine(dataDirectory, SnapshotFolder);

        internal static string SnapshotDirectory(string dataDirectory, string transaction)
        {
            if (!Guid.TryParseExact(transaction, "B", out Guid id))
                throw new ArgumentException("Invalid upgrade transaction ID.", nameof(transaction));
            return Path.Combine(SnapshotRoot(dataDirectory), Canonical(id));
        }

        // Step 3. Refuses before any change until the marker is written; after that a
        // failure returns to the MSI, which rolls back and runs /msi-upgrade-restore.
        // The snapshot is taken last: the running service saves config while applying
        // a change, so only a stopped service with no controller left gives a
        // consistent copy.
        internal static void Stop(string transaction, IUpgradeService service,
            Action authenticate, Action requirePrerequisites, Action<string> writeMarker,
            Func<TimeSpan> elapsed, Action<TimeSpan> delay,
            Action terminateControllers, Action snapshot)
        {
            authenticate();
            if (!service.Registered)
                throw new InvalidOperationException("The SecureWall service is not registered, so this installation cannot be upgraded in place. Remove SecureWall explicitly, then install the new version.");
            requirePrerequisites();
            writeMarker(transaction);
            // The start mode stays Automatic so a reboot still starts whatever
            // service is present; only SCM's crash restarts are suppressed mid-swap.
            service.SetRestartOnFailure(false);
            if (!ServiceLifecyclePolicy.StopGracefully(() => service.State, service.Stop, elapsed, delay))
                throw new InvalidOperationException("The SecureWall service did not stop within the maintenance deadline. The upgrade was rolled back.");
            terminateControllers();
            snapshot();
        }

        // Step 7 preconditions. A missing snapshot means step 3 did not run for this
        // transaction, so nothing proves the old service stopped and saved its policy.
        internal static void RequireStartable(string transaction, bool registered, LifecycleServiceState state,
            bool snapshotComplete, string? marker)
        {
            if (!registered)
                throw new InvalidOperationException("The SecureWall service is not registered. The upgrade cannot start it.");
            if (state != LifecycleServiceState.Stopped)
                throw new InvalidOperationException("The SecureWall service must be stopped before the upgraded version starts; it is " + state + ".");
            if (!snapshotComplete)
                throw new InvalidOperationException("No complete policy snapshot exists for upgrade " + transaction + ". The previous version's stop step did not finish.");
            if (!OwnsMarker(marker, transaction))
                throw new InvalidOperationException("The upgrade-in-progress marker does not name upgrade " + transaction + ".");
        }

        // After Start(): Running is success. Stopped means the service exited during
        // startup; restart-on-failure is still off, so SCM will not try again.
        internal static bool WaitForRunning(Func<LifecycleServiceState> state, TimeSpan timeout,
            Func<TimeSpan> elapsed, Action<TimeSpan> delay)
        {
            LifecycleServiceState last = LifecycleServiceState.StartPending;
            ServiceLifecyclePolicy.WaitUntil(() =>
            {
                last = state();
                return last == LifecycleServiceState.Running || last == LifecycleServiceState.Stopped;
            }, timeout, elapsed, delay);
            return last == LifecycleServiceState.Running;
        }

        internal enum RestoreAction { Refuse, StartOnly, RestoreAndStart }

        // Step 2, the old executable on rollback. Without a complete snapshot (step 3
        // failed before taking it) the policy files were never touched; only the
        // service needs starting again, whoever's marker is present. A snapshot is
        // restored only while no other upgrade's marker is present.
        internal static RestoreAction Restore(string transaction, string? marker, bool snapshotComplete)
        {
            if (!snapshotComplete) return RestoreAction.StartOnly;
            if (!string.IsNullOrWhiteSpace(marker) && !OwnsMarker(marker, transaction)) return RestoreAction.Refuse;
            return RestoreAction.RestoreAndStart;
        }

        // Replaces any earlier snapshot of the same transaction (a retried MSI keeps
        // its ProductCode). The manifest is written last, so a snapshot cut short by a
        // power loss is never restored.
        internal static void TakeSnapshot(IUpgradeFiles files, string dataDirectory, string transaction)
        {
            string directory = SnapshotDirectory(dataDirectory, transaction);
            if (files.DirectoryExists(directory)) files.DeleteDirectory(directory);
            files.CreateDirectory(directory);
            var present = new List<string>();
            foreach (string name in SnapshotNames)
            {
                string live = Path.Combine(dataDirectory, name);
                if (!files.FileExists(live)) continue;
                files.WriteFile(Path.Combine(directory, name), files.ReadFile(live), true);
                present.Add(name);
            }
            files.WriteFile(Path.Combine(directory, SnapshotManifest), Encoding.UTF8.GetBytes(FormatManifest(present)), true);
        }

        internal static bool SnapshotComplete(IUpgradeFiles files, string dataDirectory, string transaction) =>
            files.FileExists(Path.Combine(SnapshotDirectory(dataDirectory, transaction), SnapshotManifest));

        // Brings every snapshot file back byte for byte and deletes live files that
        // did not exist when the snapshot was taken, then deletes the snapshot.
        internal static void RestoreSnapshot(IUpgradeFiles files, string dataDirectory, string transaction)
        {
            string directory = SnapshotDirectory(dataDirectory, transaction);
            ISet<string> present = ParseManifest(Encoding.UTF8.GetString(files.ReadFile(Path.Combine(directory, SnapshotManifest))));
            foreach (string name in SnapshotNames)
            {
                string live = Path.Combine(dataDirectory, name);
                if (present.Contains(name))
                    files.WriteFile(live, files.ReadFile(Path.Combine(directory, name)), IsSecret(name));
                else if (files.FileExists(live))
                    files.DeleteFile(live);
            }
            files.DeleteDirectory(directory);
        }

        // Snapshot directories that no running upgrade can restore. keep is the
        // transaction whose snapshot must survive, or null to delete them all.
        // Only GUID-named directories are SecureWall's; anything else is left alone.
        internal static string[] StaleSnapshots(IEnumerable<string> directories, string? keep)
        {
            var stale = new List<string>();
            foreach (string directory in directories)
            {
                string name = Path.GetFileName(directory.TrimEnd('\\', '/'));
                if (!Guid.TryParseExact(name, "B", out Guid id)) continue;
                if (keep != null && string.Equals(Canonical(id), keep, StringComparison.OrdinalIgnoreCase)) continue;
                stale.Add(directory);
            }
            return stale.ToArray();
        }

        // Step 9 deletes every snapshot, its own included, except one that a marker
        // naming another transaction still points at.
        internal static string? CommitKeeps(string? marker, string transaction) =>
            string.IsNullOrWhiteSpace(marker) || OwnsMarker(marker, transaction) ? null : marker!.Trim();

        internal static void DeleteStaleSnapshots(IUpgradeFiles files, string dataDirectory, string? keep)
        {
            string root = SnapshotRoot(dataDirectory);
            if (!files.DirectoryExists(root)) return;
            foreach (string directory in StaleSnapshots(files.Directories(root), keep))
                files.DeleteDirectory(directory);
            if (keep == null && files.Directories(root).Length == 0)
                files.DeleteDirectory(root);
        }

        internal static string FormatManifest(IEnumerable<string> names) =>
            "SecureWall upgrade snapshot 1\n" + string.Join("\n", names) + "\n";

        internal static ISet<string> ParseManifest(string text)
        {
            string[] lines = text.Replace("\r", "").Split('\n');
            if (lines.Length < 1 || lines[0] != "SecureWall upgrade snapshot 1")
                throw new InvalidOperationException("The upgrade snapshot manifest has an unknown format.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                if (Array.IndexOf(SnapshotNames, lines[i]) < 0 || !names.Add(lines[i]))
                    throw new InvalidOperationException("The upgrade snapshot manifest names an unexpected file: " + lines[i]);
            }
            return names;
        }

        private static bool IsSecret(string name) => Array.IndexOf(SecretNames, name) >= 0;
    }
}
