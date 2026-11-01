using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
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
        void MoveDirectory(string path, string destination);
        string[] Directories(string path);
    }

    // Progress of the old version's stop step, kept in the marker. The restore step
    // decides from it, never from the service state alone: after a failed abort or a
    // reboot mid-swap, a Running service can be the new binary. Restored is written
    // by the restore step once the live files are the old version's again.
    internal enum UpgradePhase { Unknown, Stopping, Stopped, SnapshotComplete, Restored }

    internal readonly struct UpgradeMarkerState
    {
        internal readonly string Transaction;
        internal readonly UpgradePhase Phase;
        internal UpgradeMarkerState(string transaction, UpgradePhase phase) { Transaction = transaction; Phase = phase; }
    }

    // What the restore step does (docs/SECURITY.md, "In-place upgrade").
    internal enum RestorePlan
    {
        // The old service never stopped: refresh shipped data and make sure it runs.
        StartOnly,
        // Stopped, but no finished snapshot, or a restore that already finished: the
        // live files are the old version's own. Discard this transaction's snapshot
        // and start the old service.
        StopDiscardStart,
        // A verified snapshot: stop whatever runs, restore it as an exact set, start.
        StopRestoreStart,
        // The snapshot was finished but is missing or does not verify, so the new
        // version may have rewritten the live files. Stop whatever runs, keep the
        // marker and snapshot, do not start the old service.
        StopAndHold,
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

        // REG_SZ under ProductRegistryPolicy.KeyPath (64-bit view), written by
        // /msi-upgrade-stop before it stops anything: "<transaction>|<phase>".
        internal const string MarkerValue = "UpgradeInProgress";
        internal const string SnapshotFolder = "upgrade";
        internal const string SnapshotManifest = "snapshot.sha256";
        private const string ManifestHeader = "SecureWall upgrade snapshot 2";

        // Every machine-data file the service writes as policy: configuration, its
        // recovery journal, the password hash and the original hosts backup.
        // profiles.json and hosts.bck are shipped data and follow the binaries
        // through InstallDefaults.
        internal static readonly string[] SnapshotNames = { "config", "config.recovery", "pwd", "hosts.orig" };
        internal static readonly string[] SecretNames = { "pwd" };

        internal const string InProgressMessage =
            "SecureWall is being upgraded. Setup starts the SecureWall service again when it finishes; connections to other computers stay blocked until then. The SecureWall tray returns when setup finishes or at your next sign-in.";

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

        private static readonly string[] PhaseNames = { "unknown", "stopping", "stopped", "snapshot-complete", "restored" };

        internal static string FormatMarker(string transaction, UpgradePhase phase) =>
            transaction + "|" + PhaseNames[(int)phase];

        // A marker whose transaction is not a GUID is not SecureWall's to interpret;
        // an unknown phase reads as Unknown, which the restore step treats like a
        // finished snapshot (it never starts the old service on unverified files).
        internal static UpgradeMarkerState? ParseMarker(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string[] parts = value!.Trim().Split('|');
            if (!Guid.TryParseExact(parts[0], "B", out Guid id)) return null;
            UpgradePhase phase = UpgradePhase.Unknown;
            if (parts.Length == 2)
                for (int i = 1; i < PhaseNames.Length; i++)
                    if (string.Equals(parts[1], PhaseNames[i], StringComparison.Ordinal)) phase = (UpgradePhase)i;
            return new UpgradeMarkerState(Canonical(id), phase);
        }

        internal static bool OwnsMarker(string? marker, string transaction) =>
            ParseMarker(marker) is UpgradeMarkerState state &&
            string.Equals(state.Transaction, transaction, StringComparison.OrdinalIgnoreCase);

        // Controller repair, /install outside the upgrade modes and the service's own
        // recovery-settings refresh. A marker counts only while Windows Installer runs
        // an installation, so a marker left by a power loss never blocks SCM's boot
        // start, a manual sc start or a later repair. The upgrade's own starter
        // passes its transaction ID.
        internal static bool BlocksStart(string? marker, bool installerRunning, string? transaction = null) =>
            !string.IsNullOrWhiteSpace(marker) && installerRunning &&
            (transaction == null || !OwnsMarker(marker, transaction));

        // While an upgrade runs, only its commit and rollback steps change the SCM
        // restart actions. A service start in between (the upgraded service's first
        // start included) leaves them off, so no restart can be queued mid-swap.
        internal static bool UpgradeOwnsRecoverySettings(string? marker, bool installerRunning) =>
            BlocksStart(marker, installerRunning);

        internal static string SnapshotRoot(string dataDirectory) => Path.Combine(dataDirectory, SnapshotFolder);

        internal static string SnapshotDirectory(string dataDirectory, string transaction)
        {
            if (!Guid.TryParseExact(transaction, "B", out Guid id))
                throw new ArgumentException("Invalid upgrade transaction ID.", nameof(transaction));
            return Path.Combine(SnapshotRoot(dataDirectory), Canonical(id));
        }

        // Step 3. Refuses before any change until the marker is written; after that a
        // failure returns to the MSI, which rolls back and runs /msi-upgrade-restore.
        // A snapshot left by an earlier attempt of the same transaction (a retried MSI
        // keeps its ProductCode) is set aside once every check passed, so this
        // attempt's rollback can only restore a snapshot this attempt took.
        //
        // Only a Running service is accepted: one stopped after a crash may have an
        // SCM restart queued, which no later setting change cancels. Failure actions
        // are evaluated when a failure happens, so once they are off a crash queues
        // nothing; the second Running check catches a crash just before that.
        // The snapshot is taken only after the stop and after every controller is
        // gone, with the service confirmed Stopped before the copy and again before
        // the hash manifest, which is the last thing written.
        internal static void Stop(string transaction, IUpgradeService service,
            Action authenticate, Action setAsidePriorSnapshot, Action requirePrerequisites,
            Action<UpgradePhase> writeMarker, Func<TimeSpan> elapsed, Action<TimeSpan> delay,
            Action terminateControllers, Action copySnapshot, Action writeManifest)
        {
            authenticate();
            if (!service.Registered)
                throw new InvalidOperationException("The SecureWall service is not registered, so this installation cannot be upgraded in place. Remove SecureWall explicitly, then install the new version.");
            RequireRunning(service);
            requirePrerequisites();
            setAsidePriorSnapshot();
            writeMarker(UpgradePhase.Stopping);
            service.SetRestartOnFailure(false);
            RequireRunning(service);
            if (!ServiceLifecyclePolicy.StopGracefully(() => service.State, service.Stop, elapsed, delay))
                throw new InvalidOperationException("The SecureWall service did not stop within the maintenance deadline. The upgrade was rolled back.");
            terminateControllers();
            writeMarker(UpgradePhase.Stopped);
            RequireStopped(service);
            copySnapshot();
            RequireStopped(service);
            writeManifest();
            writeMarker(UpgradePhase.SnapshotComplete);
        }

        private static void RequireRunning(IUpgradeService service)
        {
            LifecycleServiceState state = service.State;
            if (state != LifecycleServiceState.Running)
                throw new InvalidOperationException("The SecureWall service is " + state + ", not Running. A service stopped after a failure may have a restart queued that could start it during the upgrade. Start it (sc start SecureWall from an elevated prompt, or restart Windows), then run setup again.");
        }

        private static void RequireStopped(IUpgradeService service)
        {
            LifecycleServiceState state = service.State;
            if (state != LifecycleServiceState.Stopped)
                throw new InvalidOperationException("The SecureWall service started again during the upgrade (it is " + state + "), so its policy files cannot be copied safely.");
        }

        // Step 7 preconditions: proof that step 3 finished for this transaction.
        internal static void RequireStartable(string transaction, bool registered, LifecycleServiceState state,
            bool snapshotVerified, string? marker)
        {
            if (!registered)
                throw new InvalidOperationException("The SecureWall service is not registered. The upgrade cannot start it.");
            if (state != LifecycleServiceState.Stopped)
                throw new InvalidOperationException("The SecureWall service must be stopped before the upgraded version starts; it is " + state + ".");
            if (!(ParseMarker(marker) is UpgradeMarkerState m) || !string.Equals(m.Transaction, transaction, StringComparison.OrdinalIgnoreCase) || m.Phase != UpgradePhase.SnapshotComplete)
                throw new InvalidOperationException("The upgrade-in-progress marker does not show a finished stop step for upgrade " + transaction + ".");
            if (!snapshotVerified)
                throw new InvalidOperationException("No verified policy snapshot exists for upgrade " + transaction + ". The previous version's stop step did not finish.");
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

        // Step 2, the old executable on rollback. Idempotent: it may run after step 3
        // failed at any point, after a failed abort, or after a reboot mid-swap.
        internal static RestorePlan Restore(string transaction, string? marker, bool serviceRunning, bool snapshotVerified)
        {
            if (!(ParseMarker(marker) is UpgradeMarkerState m) || !string.Equals(m.Transaction, transaction, StringComparison.OrdinalIgnoreCase))
                return RestorePlan.StartOnly;
            if ((m.Phase == UpgradePhase.Stopping || m.Phase == UpgradePhase.Restored) && serviceRunning)
                return RestorePlan.StartOnly;
            if (m.Phase == UpgradePhase.Restored)
                return RestorePlan.StopDiscardStart;
            if (snapshotVerified)
                return RestorePlan.StopRestoreStart;
            if (m.Phase == UpgradePhase.Stopping || m.Phase == UpgradePhase.Stopped)
                return RestorePlan.StopDiscardStart;
            return RestorePlan.StopAndHold;
        }

        // Step 3 of a retried MSI: an earlier attempt's rollback that has yet to
        // restore its verified snapshot is finished by this attempt's step 2, so
        // step 3 refuses instead of setting that snapshot aside.
        internal static void RequireNoPendingRestore(string transaction, string? marker, bool snapshotVerified)
        {
            if (Restore(transaction, marker, true, snapshotVerified) == RestorePlan.StopRestoreStart)
                throw new InvalidOperationException("An earlier attempt of this upgrade did not finish rolling back. Setup now restores its policy snapshot and starts the previous version; run setup again afterwards.");
        }

        // Moves an earlier attempt's snapshot of this transaction to
        // "<transaction>.previous-<suffix>". It is never restored or cleaned up
        // automatically (not a GUID name); it stays as recovery evidence.
        internal static string? SetAsidePriorSnapshot(IUpgradeFiles files, string dataDirectory, string transaction, string suffix)
        {
            string directory = SnapshotDirectory(dataDirectory, transaction);
            if (!files.DirectoryExists(directory)) return null;
            string kept = directory + ".previous-" + suffix;
            files.MoveDirectory(directory, kept);
            return kept;
        }

        // Copies the policy files. Nothing marks the copy complete yet.
        internal static void CopySnapshot(IUpgradeFiles files, string dataDirectory, string transaction)
        {
            string directory = SnapshotDirectory(dataDirectory, transaction);
            if (files.DirectoryExists(directory)) files.DeleteDirectory(directory);
            files.CreateDirectory(directory);
            foreach (string name in SnapshotNames)
            {
                string live = Path.Combine(dataDirectory, name);
                if (!files.FileExists(live)) continue;
                files.WriteFile(Path.Combine(directory, name), files.ReadFile(live), true);
            }
        }

        // Hashes the copies as they are on disk and writes the manifest. A snapshot
        // without a manifest, or whose files no longer match it, is never restored.
        internal static void WriteManifest(IUpgradeFiles files, string dataDirectory, string transaction)
        {
            string directory = SnapshotDirectory(dataDirectory, transaction);
            var lines = new StringBuilder(ManifestHeader).Append('\n');
            foreach (string name in SnapshotNames)
            {
                string copy = Path.Combine(directory, name);
                if (files.FileExists(copy)) lines.Append(name).Append(' ').Append(Sha256(files.ReadFile(copy))).Append('\n');
            }
            files.WriteFile(Path.Combine(directory, SnapshotManifest), Encoding.UTF8.GetBytes(lines.ToString()), true);
        }

        internal static void TakeSnapshot(IUpgradeFiles files, string dataDirectory, string transaction)
        {
            CopySnapshot(files, dataDirectory, transaction);
            WriteManifest(files, dataDirectory, transaction);
        }

        // The listed files with their hashes, or null when the snapshot is absent,
        // unreadable, malformed or does not match its manifest. A copy the manifest
        // does not list fails too: restoring would delete that live file.
        internal static IDictionary<string, string>? VerifiedSnapshot(IUpgradeFiles files, string dataDirectory, string transaction)
        {
            try
            {
                string directory = SnapshotDirectory(dataDirectory, transaction);
                string manifest = Path.Combine(directory, SnapshotManifest);
                if (!files.FileExists(manifest)) return null;
                IDictionary<string, string> entries = ParseManifest(Encoding.UTF8.GetString(files.ReadFile(manifest)));
                foreach (string name in SnapshotNames)
                {
                    string copy = Path.Combine(directory, name);
                    bool listed = entries.TryGetValue(name, out string? hash);
                    if (listed != files.FileExists(copy))
                        return null;
                    if (listed && !string.Equals(Sha256(files.ReadFile(copy)), hash, StringComparison.Ordinal))
                        return null;
                }
                return entries;
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is InvalidOperationException)
            {
                return null;
            }
        }

        // Exact set: every covered file the snapshot lists is written back byte for
        // byte (pwd with the secret DACL), every covered file it does not list is
        // deleted. The caller verified it first, and deletes it only after recording
        // phase Restored, so a rerun interrupted before then restores it again.
        internal static void RestoreSnapshot(IUpgradeFiles files, string dataDirectory, string transaction, IDictionary<string, string> verified)
        {
            string directory = SnapshotDirectory(dataDirectory, transaction);
            foreach (string name in SnapshotNames)
            {
                string live = Path.Combine(dataDirectory, name);
                if (verified.ContainsKey(name))
                    files.WriteFile(live, files.ReadFile(Path.Combine(directory, name)), IsSecret(name));
                else if (files.FileExists(live))
                    files.DeleteFile(live);
            }
        }

        internal static void DeleteSnapshot(IUpgradeFiles files, string dataDirectory, string transaction)
        {
            string directory = SnapshotDirectory(dataDirectory, transaction);
            if (files.DirectoryExists(directory)) files.DeleteDirectory(directory);
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
            ParseMarker(marker) is UpgradeMarkerState m && !string.Equals(m.Transaction, transaction, StringComparison.OrdinalIgnoreCase)
                ? m.Transaction : null;

        internal static void DeleteStaleSnapshots(IUpgradeFiles files, string dataDirectory, string? keep)
        {
            string root = SnapshotRoot(dataDirectory);
            if (!files.DirectoryExists(root)) return;
            foreach (string directory in StaleSnapshots(files.Directories(root), keep))
                files.DeleteDirectory(directory);
            if (keep == null && files.Directories(root).Length == 0)
                files.DeleteDirectory(root);
        }

        internal static IDictionary<string, string> ParseManifest(string text)
        {
            string[] lines = text.Replace("\r", "").Split('\n');
            if (lines.Length < 1 || lines[0] != ManifestHeader)
                throw new InvalidOperationException("The upgrade snapshot manifest has an unknown format.");
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                string[] parts = lines[i].Split(' ');
                if (parts.Length != 2 || Array.IndexOf(SnapshotNames, parts[0]) < 0 || parts[1].Length != 64 || entries.ContainsKey(parts[0]))
                    throw new InvalidOperationException("The upgrade snapshot manifest has an unexpected entry: " + lines[i]);
                entries.Add(parts[0], parts[1]);
            }
            return entries;
        }

        internal static string Sha256(byte[] contents)
        {
            using var sha = SHA256.Create();
            var hex = new StringBuilder(64);
            foreach (byte b in sha.ComputeHash(contents)) hex.Append(b.ToString("x2"));
            return hex.ToString();
        }

        private static bool IsSecret(string name) => Array.IndexOf(SecretNames, name) >= 0;
    }
}
