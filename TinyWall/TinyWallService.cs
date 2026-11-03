using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Net;
using System.Net.NetworkInformation;
using System.Management;
using System.Threading;
using System.Linq;
using pylorak.Windows;
using pylorak.Windows.Services;
using pylorak.Windows.WFP;
using pylorak.Windows.WFP.Interop;
using pylorak.Utilities;
using pylorak.TinyWall.Prompting;

namespace pylorak.TinyWall
{
    public sealed class TinyWallServer : IDisposable
    {
        private readonly ServiceRuntimeDiagnostics Diagnostics;
        private bool DiagnosticSettingLoaded;
        private readonly DiagnosticFilterSet PortBlocklistFilterIds = new();
        private List<ulong>? PendingPortBlocklistFilterIds;
        // Display-only block reasons for Network Activity; published only after commit.
        private readonly FilterGroupMap FilterGroups = new();
        private List<KeyValuePair<ulong, FilterGroup>>? PendingFilterGroups;
        private bool EffectiveHostsBlocklist;
        private bool EffectivePortBlocklist;
        private bool PortBlocklistDiagnosticsAvailable;
        private int LastDiagnosticAuditState = -1;
        private long LastDiagnosticRecordErrors;
        private readonly CoalescedDiagnostic AuditRecordReports = new();
        private enum FilterWeights : ulong
        {
            Blocklist = 9000000,
            RawSocketPermit = 8000000,
            RawSocketBlock = 7000000,
            OwnImagePermit = Prompting.AiExplainEgressPolicy.PermitWeight,
            UserBlock = 6000000,
            UserPermit = 5000000,
            DefaultPermit = 4000000,
            DefaultBlock = 3000000,
        }

        private static readonly Guid SECUREWALL_PROVIDER_KEY = new("{053FC8F9-9052-4B2F-9B24-7DE3A2BED6E0}");

        private readonly BlockingCollection<TwRequest> Q = new(32);
        private readonly PipeServerEndpoint ServerPipe;
        private readonly Timer MinuteTimer;
        private readonly Timer PromptCandidateTimer;

        private readonly CircularBuffer<FirewallLogEntry> FirewallLogEntries = new(500);
        private readonly FileLocker FileLocker = new();
        private readonly HostsFileManager HostsFileManager = new();
        private readonly UserActivityTimeout ControllerActivity = new(SystemClock.Instance, TimeSpan.FromMinutes(10));
        private DateTime LastRuleReloadTime = DateTime.Now;

        // Security event 5157 subscription for prompt attribution
        private readonly FirewallLogWatcher LogWatcher;

        // Only runtime IDs belonging to outbound default-block filters may create prompts.
        private readonly PromptableFilterSet PromptableFilterIds = new();
        private readonly PromptQueue BlockedPromptQueue = new(SystemClock.Instance);
        private readonly DropCandidateBuffer DropCandidates = new(SystemClock.Instance);
        private readonly CorrelatedDropBatch CorrelatedDrops;
        private readonly CoalescedDiagnostic CandidateOverflows = new();
        private readonly CoalescedDiagnostic PromptOverflows = new();
        private readonly CoalescedDiagnostic ServiceSnapshotErrors = new();
        private readonly CoalescedDiagnostic ServiceSidQueryErrors = new();
        private readonly CoalescedDiagnostic UnavailableVolumeRules = new();
        private readonly ServiceExecutableCatalog ServiceExecutables = new();
        // Check for updates: transient per-user TCP 443 permits. Enforcement thread only.
        private readonly UpdateNetworkLeases UpdateLeases = new(new MonotonicClock());

        // Context for auto rule inheritance
        private readonly object InheritanceGuard = new();
        private HashSet<string> UserSubjectExes = new(StringComparer.OrdinalIgnoreCase);        // All executables with pre-configured rules.
        private HashSet<string> UserSubjectPackages = new(StringComparer.OrdinalIgnoreCase);    // All AppContainer package SIDs with pre-configured rules.
        private HashSet<string> UserExecutableWideExes = new(StringComparer.OrdinalIgnoreCase); // Executables with a rule for the whole executable, not one service.
        // UserSubjectExes as last committed (app and special-profile rules); never changed after
        // publication, so the prompt timer thread reads it without InheritanceGuard.
        private volatile HashSet<string> CommittedRuleExecutables = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<FirewallExceptionV3>> ChildInheritance = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, HashSet<string>> ChildInheritedSubjectExes = new(StringComparer.OrdinalIgnoreCase);   // Executables that have been already auto-whitelisted due to inheritance
        private readonly ThreadThrottler FirewallThreadThrottler = new(Thread.CurrentThread, ThreadPriority.Highest, false);
        private StringBuilder? ProcessStartWatcher_Sbuilder;

        private bool RunService = false;
        private bool DisplayCurrentlyOn = true;
        private readonly ServerState VisibleState = new();

        // Replaced only by the enforcement thread when a revoked session is rebuilt.
        private Engine WfpEngine = CreateRuntimeEngine();
        private IDisposable? RuntimeEventSubscription;
        private volatile bool RuntimeStopping;
        private bool RuntimeSessionRevoked;
        private FirewallMode RevokedMode = FirewallMode.Unknown;
        private readonly RecoveryBackoff ReloadRetry = new();
        private readonly RecoveryBackoff SessionRebuild = new();
        private static readonly Stopwatch RecoveryClock = Stopwatch.StartNew();
        // Facts about the committed runtime policy, owned by the enforcement thread.
        private bool LastInstallCommitted;
        private readonly AddressVerification Addresses = new();
        private bool VolumeMappingChanged;
        private bool CommittedDisplayRestricted;
        internal bool Ready { get; private set; }
        private readonly List<Guid> RuntimeFilterKeys = new();
        private List<Guid>? PendingRuntimeFilterKeys;
        private ServerConfiguration? ApplyingConfiguration;
        private FirewallMode? ApplyingMode;
        // Set only while ApplyConfiguration applies an AI-egress-only change (see AiExplainEgressPolicy).
        private bool PreservePromptTokens;
        private bool BaselineInstalled;
        private ServerConfiguration PolicyConfiguration => ApplyingConfiguration ?? ActiveConfig.Service;
        private FirewallMode PolicyMode => ApplyingMode ?? VisibleState.Mode;
        private readonly ManagementEventWatcher ProcessStartWatcher = new(new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
        private readonly EventMerger RuleReloadEventMerger = new(1000);

        // LocalSubnet, DefaultGateway and DNS address sets, in that order.
        private readonly AddressConditionSets<IpAddrMask> RemoteAddressSets = new(3);
        private readonly FilterConditionList LocalSubnetFilterConditions = new();
        private readonly FilterConditionList GatewayFilterConditions = new();
        private readonly FilterConditionList DnsFilterConditions = new();

        private static string? GetPackageSid(uint processId)
        {
            try
            {
                return ProcessManager.GetAppContainerSid(processId);
            }
            catch
            {
                // A process we cannot inspect counts as having no package
                return null;
            }
        }

        // Ancestors of a snapshot process, nearest first, for RuleInheritance.
        private static IEnumerable<(string? Path, string? PackageSid)> SnapshotAncestors(
            Dictionary<uint, ProcessSnapshotEntry> procTree, ProcessSnapshotEntry child, HashSet<uint> pidsChecked, Func<uint, string?> packageOf)
        {
            for (var parentEntry = child; ;)
            {
                long childCreationTime = parentEntry.CreationTime;
                if (procTree.TryGetValue(parentEntry.ParentProcessId, out var val))
                    parentEntry = val;
                else
                    // We reached top of process tree (with non-existing parent)
                    yield break;

                // Check if what we have is really the parent, or just a reused PID
                if (parentEntry.CreationTime > childCreationTime)
                    // We reached the top of the process tree (with non-existing parent)
                    yield break;

                if (parentEntry.ProcessId == 0)
                    // We reached top of process tree (with idle process)
                    yield break;

                if (pidsChecked.Contains(parentEntry.ProcessId))
                    // We've been here before, damn it. Avoid looping eternally...
                    yield break;

                pidsChecked.Add(parentEntry.ProcessId);

                yield return (parentEntry.ImagePath, packageOf(parentEntry.ProcessId));
            }
        }

        // Ancestors of a running process, nearest first, for RuleInheritance.
        private IEnumerable<(string? Path, string? PackageSid)> RunningAncestors(uint pid, Func<uint, string?> packageOf)
        {
            // This list will hold parents that we already checked for a process.
            // Used to avoid infinite loop when parent-PID info is unreliable.
            var pidsChecked = new HashSet<uint>();

            for (var parentPid = pid; ;)
            {
                if (!ProcessManager.GetParentProcess(parentPid, ref parentPid))
                    // We reached the top of the process tree (with non-existent parent)
                    yield break;

                if (parentPid == 0)
                    // We reached top of process tree (with idle process)
                    yield break;

                if (pidsChecked.Contains(parentPid))
                    // We've been here before, damn it. Avoid looping eternally...
                    yield break;

                pidsChecked.Add(parentPid);

                yield return (ProcessManager.GetProcessPath(parentPid, ref ProcessStartWatcher_Sbuilder), packageOf(parentPid));
            }
        }

        private List<RuleDef> AssembleActiveRules(List<RuleDef> rawSocketExceptions)
        {
            using var timer = new HierarchicalStopwatch("AssembleActiveRules()");
            var rules = new List<RuleDef>();
            var ModeId = Guid.NewGuid();

            // Isolation takes precedence over every configured exception, including database permits.
            if (PolicyMode == FirewallMode.BlockAll)
            {
                rules.Add(new RuleDef(ModeId, "Block everything", GlobalSubject.Instance, RuleAction.Block,
                    RuleDirection.InOut, Protocol.Any, (ulong)FilterWeights.DefaultBlock));
                return rules;
            }

            // Do we want to let local traffic through?
            if (EnforcementPolicy.OptionalPermitEnabled(PolicyMode == FirewallMode.BlockAll, PolicyConfiguration.ActiveProfile.AllowLocalSubnet))
            {
                var def = new RuleDef(ModeId, "Allow local subnet", GlobalSubject.Instance, RuleAction.Allow, RuleDirection.InOut, Protocol.Any, (ulong)FilterWeights.DefaultPermit)
                {
                    RemoteAddresses = RuleDef.LOCALSUBNET_ID
                };
                rules.Add(def);
            }

            // Do we want to block known malware ports?
            if (PolicyConfiguration.Blocklists.EnableBlocklists && PolicyConfiguration.Blocklists.EnablePortBlocklist)
            {
                var exceptions = new List<FirewallExceptionV3>();
                exceptions.AddRange(CollectExceptionsForAppByName("Malware Ports"));
                foreach (var ex in exceptions)
                {
                    ex.RegenerateId();
                    GetRulesForException(ex, rules, rawSocketExceptions, (ulong)FilterWeights.DefaultPermit, (ulong)FilterWeights.Blocklist);
                }
            }

            // Rules specific to the selected firewall mode
            bool needUserRules = true;
            switch (PolicyMode)
            {
                case FirewallMode.AllowOutgoing:
                    {
                        // Block everything
                        var def = new RuleDef(ModeId, "Block everything", GlobalSubject.Instance, RuleAction.Block, RuleDirection.InOut, Protocol.Any, (ulong)FilterWeights.DefaultBlock);
                        rules.Add(def);

                        // Allow outgoing
                        def = new RuleDef(ModeId, "Allow outbound", GlobalSubject.Instance, RuleAction.Allow, RuleDirection.Out, Protocol.Any, (ulong)FilterWeights.DefaultPermit);
                        rules.Add(def);
                        break;
                    }
                case FirewallMode.Disabled:
                    {
                        // We won't need application exceptions
                        needUserRules = false;

                        // Add rule to explicitly allow everything
                        var def = new RuleDef(ModeId, "Allow everything", GlobalSubject.Instance, RuleAction.Allow, RuleDirection.InOut, Protocol.Any, (ulong)FilterWeights.DefaultPermit);
                        rules.Add(def);
                        break;
                    }
                case FirewallMode.Normal:
                    {
                        // Block all by default
                        var def = new RuleDef(ModeId, "Block everything", GlobalSubject.Instance, RuleAction.Block, RuleDirection.InOut, Protocol.Any, (ulong)FilterWeights.DefaultBlock);
                        rules.Add(def);
                        break;
                    }
            }

            if (needUserRules)
            {
                var UserExceptions = new List<FirewallExceptionV3>();

                // Collect all applications exceptions
                UserExceptions.AddRange(PolicyConfiguration.ActiveProfile.AppExceptions);

                // Collect all special exceptions

                foreach (string appName in PolicyConfiguration.ActiveProfile.SpecialExceptions.Where(name => name != "TinyWall"))
                    UserExceptions.AddRange(CollectExceptionsForAppByName(appName));

                // Convert exceptions to rules
                foreach (FirewallExceptionV3 ex in UserExceptions)
                {
                    if (EnforcementPolicy.IsExpired(ex.CreationDate, (int)ex.Timer, DateTime.Now))
                        continue;
                    if (ex.Subject is ExecutableSubject exe)
                    {
                        string exePath = exe.ExecutablePath;
                        UserSubjectExes.Add(exePath);
                        if (ex.Subject.SubjectType == SubjectType.Executable)
                            UserExecutableWideExes.Add(exePath);
                        // A service rule's path is its host (e.g. svchost.exe). Inheritance matches ancestors
                        // by path alone, so it would reach the children of every instance of that host.
                        if (ex.ChildProcessesInherit && ex.Subject.SubjectType != SubjectType.Service)
                        {
                            // We might have multiple rules with the same exePath, so we maintain a list of exceptions
                            if (!ChildInheritance.ContainsKey(exePath))
                                ChildInheritance.Add(exePath, new List<FirewallExceptionV3>());
                            ChildInheritance[exePath].Add(ex);
                        }
                    }
                    else if (ex.Subject is AppContainerSubject uwp)
                    {
                        UserSubjectPackages.Add(uwp.Sid);
                    }

                    GetRulesForException(ex, rules, rawSocketExceptions, (ulong)FilterWeights.UserPermit, (ulong)FilterWeights.UserBlock);
                }

                if (ChildInheritance.Count != 0)
                {
                    timer.NewSubTask("Rule inheritance processing");

                    var sbuilder = new StringBuilder(1024);
                    var procTree = new Dictionary<uint, ProcessSnapshotEntry>();
                    foreach (var p in ProcessManager.CreateToolhelp32SnapshotExtended())
                        procTree.Add(p.ProcessId, p);

                    // This list will hold parents that we already checked for a process.
                    // Used to avoid inf. loop when parent-PID info is unreliable.
                    var pidsChecked = new HashSet<uint>();

                    // Package SIDs, looked up at most once per process and only if a package has a rule
                    var packageSids = new Dictionary<uint, string?>();
                    string? PackageOf(uint processId)
                    {
                        if (UserSubjectPackages.Count == 0)
                            return null;
                        if (!packageSids.TryGetValue(processId, out string? sid))
                        {
                            sid = GetPackageSid(processId);
                            packageSids.Add(processId, sid);
                        }
                        return sid;
                    }

                    foreach (var pair in procTree)
                    {
                        pidsChecked.Clear();

                        string procPath = pair.Value.ImagePath;

                        // Skip if we have no path
                        if (string.IsNullOrEmpty(procPath))
                            continue;

                        string? parentPath = RuleInheritance.FindInheritedParent(procPath, PackageOf(pair.Key),
                            SnapshotAncestors(procTree, pair.Value, pidsChecked, PackageOf),
                            UserSubjectExes, UserExecutableWideExes, UserSubjectPackages, ChildInheritance.ContainsKey,
                            p => ChildInheritedSubjectExes.TryGetValue(procPath, out var childVal) && childVal.Contains(p));

                        if (parentPath != null)
                        {
                            var subj = new ExecutableSubject(procPath);
                            foreach (var userEx in ChildInheritance[parentPath])
                                if (!EnforcementPolicy.IsExpired(userEx.CreationDate, (int)userEx.Timer, DateTime.Now))
                                    GetRulesForException(new FirewallExceptionV3(subj, userEx.Policy), rules, rawSocketExceptions, (ulong)FilterWeights.UserPermit, (ulong)FilterWeights.UserBlock);

                            if (!ChildInheritedSubjectExes.ContainsKey(procPath))
                                ChildInheritedSubjectExes.Add(procPath, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                            ChildInheritedSubjectExes[procPath].Add(parentPath);
                        }
                    }
                }   // if (ChildInheritance ...
            }

            bool displayBlockActive = PolicyConfiguration.ActiveProfile.DisplayOffBlock && !DisplayCurrentlyOn;
            if (displayBlockActive)
            {
                // Modify all allow-rules to only allow local subnet
                foreach (var r in rules)
                {
                    if (r.Action == RuleAction.Allow)
                    {
                        r.RemoteAddresses = RuleDef.LOCALSUBNET_ID;
                    }
                }
            }

            return rules;
        }

        private List<ulong> InstallRules(List<RuleDef> rules, List<RuleDef> rawSocketExceptions, bool useTransaction)
        {
            // Ordinary, raw-socket and incremental child rules share this boundary.
            List<RuleDef> Normalize(List<RuleDef> source) => EnforcementPolicy.NormalizeRules(source,
                rule => rule.Application, (rule, path) => rule.Application = path,
                path => PathMapper.Instance.ConvertPath(path.AsSpan(), PathFormat.NativeNt), path =>
                {
                    UnavailableVolumeRules.Record();
                    if (UnavailableVolumeRules.TryReport(DateTimeOffset.UtcNow, out long count))
                    {
                        Diagnostics.Emit(RuntimeEvent.unavailable_rule_paths, RuntimeResult.observed);
                        Utils.Log("Skipped " + count + " rules with unavailable volumes since the previous report. " +
                            "Saved rules are retained and retried on reload. Example: " + path.Substring(0, Math.Min(path.Length, 256)),
                            Utils.LOG_ID_SERVICE);
                    }
                });
            rules = Normalize(rules);
            rawSocketExceptions = Normalize(rawSocketExceptions);

            var promptableFilterIds = new List<ulong>();
            Transaction? trx = useTransaction ? WfpEngine.BeginTransaction() : null;
            var addedKeys = useTransaction ? new List<Guid>() : PendingRuntimeFilterKeys;
            if (useTransaction)
            {
                PendingRuntimeFilterKeys = addedKeys;
                try { PendingFilterGroups = new List<KeyValuePair<ulong, FilterGroup>>(); }
                catch { PendingFilterGroups = null; } // Display data cannot abort policy.
            }
            try
            {
                // Add new rules
                foreach (RuleDef r in rules)
                {
                    try
                    {
                        ConstructFilter(r, promptableFilterIds);
                    }
                    catch (Exception exception)
                    {
                        throw new InvalidOperationException($"Failed to install rule {r.ExceptionId}: {r.Name}.", exception);
                    }
                }

                // Built-in protections. WSL filters belong to the full reload only;
                // incremental child-rule calls must not add another copy.
                if (PolicyMode != FirewallMode.Disabled)
                    InstallRawSocketPermits(rawSocketExceptions);

                trx?.Commit();
                if (useTransaction)
                {
                    RuntimeFilterKeys.AddRange(addedKeys!);
                    FilterGroups.Add(PendingFilterGroups);
                }
                return promptableFilterIds;
            }
            finally
            {
                if (useTransaction)
                {
                    PendingRuntimeFilterKeys = null;
                    PendingFilterGroups = null;
                }
                trx?.Dispose();
            }

        }

        private void InstallFirewallRules()
        {
            Diagnostics.Run(RuntimeEvent.policy_enforce, InstallFirewallRulesCore);
        }

        private void InstallFirewallRulesCore()
        {
            using var timer = new HierarchicalStopwatch("InstallFirewallRules()");
            LastInstallCommitted = false;
            bool displayRestricted = PolicyConfiguration.ActiveProfile.DisplayOffBlock && !DisplayCurrentlyOn;
            ResetPromptCandidates(revokeTokens: false);
            PathMapper.Instance.RebuildCache();
            lock (InheritanceGuard)
            {
                var previousSubjects = UserSubjectExes;
                var previousPackages = UserSubjectPackages;
                var previousExecutableWide = UserExecutableWideExes;
                var previousInheritance = ChildInheritance;
                var previousInheritedSubjects = ChildInheritedSubjectExes;
                bool committed = false;
                UserSubjectExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                UserSubjectPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                UserExecutableWideExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ChildInheritance = new Dictionary<string, List<FirewallExceptionV3>>(StringComparer.OrdinalIgnoreCase);
                ChildInheritedSubjectExes = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var rawSocketExceptions = new List<RuleDef>();
                    List<RuleDef> rules = AssembleActiveRules(rawSocketExceptions);
                    using Transaction trx = WfpEngine.BeginTransaction();
                    // Persistent recovery filters and their sublayers are never removed on reload.
                    foreach (Guid key in RuntimeFilterKeys)
                        WfpEngine.UnregisterFilter(key);
                    var candidateKeys = new List<Guid>();
                    PendingRuntimeFilterKeys = candidateKeys;
                    try { PendingPortBlocklistFilterIds = new List<ulong>(); }
                    catch { PendingPortBlocklistFilterIds = null; } // Diagnostics cannot abort policy.
                    try { PendingFilterGroups = new List<KeyValuePair<ulong, FilterGroup>>(); }
                    catch { PendingFilterGroups = null; } // Display data cannot abort policy.
                    if (PolicyMode != FirewallMode.Disabled)
                    {
                        InstallPortScanProtection();
                        InstallRawSocketBlocks();
                    }
                    List<ulong> newPromptableFilterIds = InstallRules(rules, rawSocketExceptions, false);
                    if (PolicyMode != FirewallMode.Disabled)
                        InstallWsl2Filters(EnforcementPolicy.OptionalPermitEnabled(PolicyMode == FirewallMode.BlockAll, PolicyConfiguration.ActiveProfile.HasSpecialException("WSL_2")));
                    InstallAiAssistantPermit();
                    var updatePermits = InstallUpdatePermits();
                    trx.Commit();
                    committed = true;
                    LastInstallCommitted = true;
                    // Published before lease cleanup, which removes its keys (or fails closed).
                    RuntimeFilterKeys.Clear();
                    RuntimeFilterKeys.AddRange(candidateKeys);
                    CommitUpdatePermits(updatePermits);
                    PortBlocklistDiagnosticsAvailable = PendingPortBlocklistFilterIds != null && PortBlocklistFilterIds.Replace(PendingPortBlocklistFilterIds);
                    if (!PortBlocklistDiagnosticsAvailable) PortBlocklistFilterIds.Clear();
                    FilterGroups.Replace(PendingFilterGroups);
                    EffectivePortBlocklist = PolicyConfiguration.Blocklists.EnableBlocklists &&
                        PolicyConfiguration.Blocklists.EnablePortBlocklist &&
                        PolicyMode != FirewallMode.BlockAll;
                    Diagnostics.Emit(RuntimeEvent.port_blocklist_state, EffectivePortBlocklist ? RuntimeResult.enabled : RuntimeResult.disabled);
                    Diagnostics.Emit(RuntimeEvent.port_blocklist_rules, PortBlocklistDiagnosticsAvailable
                        ? (PortBlocklistFilterIds.Any ? RuntimeResult.present : RuntimeResult.absent) : RuntimeResult.failure);
                    lock (BlockedPromptQueue.SyncRoot)
                    {
                        ResetPromptCandidates(revokeTokens: !PreservePromptTokens);
                        PromptableFilterIds.Replace(PolicyMode == FirewallMode.Normal
                            ? newPromptableFilterIds : Array.Empty<ulong>());
                    }
                    CommittedRuleExecutables = UserSubjectExes;
                    LastRuleReloadTime = DateTime.Now;
                    // The committed policy now reflects the current environment.
                    Addresses.Committed();
                    VolumeMappingChanged = false;
                    CommittedDisplayRestricted = displayRestricted;
                    // Unverified addresses keep any pending retry; only an enumeration clears them.
                    if (!Addresses.Superseded)
                        ReloadRetry.Reset();
                }
                finally
                {
                    PendingRuntimeFilterKeys = null;
                    PendingPortBlocklistFilterIds = null;
                    PendingFilterGroups = null;
                    if (!committed)
                    {
                        UserSubjectExes = previousSubjects;
                        UserSubjectPackages = previousPackages;
                        UserExecutableWideExes = previousExecutableWide;
                        ChildInheritance = previousInheritance;
                        ChildInheritedSubjectExes = previousInheritedSubjects;
                        // The previous policy may stay in force; update permits do not.
                        RemoveUpdateLeases(UpdateLeases.Clear());
                    }
                }
                try
                {
                    if (ChildInheritance.Count > 0)
                        ProcessStartWatcher.Start();
                    else
                        ProcessStartWatcher.Stop();
                }
                catch (Exception exception)
                {
                    Utils.LogException(exception, Utils.LOG_ID_SERVICE);
                }
            }
        }

        private void EnsureRestrictiveBaseline()
        {
            if (!BaselineInstalled)
                Diagnostics.Run(RuntimeEvent.baseline_register, RegisterRestrictiveBaseline);
        }

        private void RegisterRestrictiveBaseline()
        {
            if (BaselineInstalled)
                return;
            using var baseline = new Engine("SecureWall Recovery Baseline", "", FWPM_SESSION_FLAGS.None, 5000);
            using var transaction = baseline.BeginTransaction();
            // TinyWall leftovers keep enforcing beside SecureWall; refuse before any change.
            RequireWfpCoexistence(baseline, "SecureWall baseline registration");
            // Atomically replace legacy persisted permits with a restrictive recovery policy.
            DeleteWfpObjects(baseline, true);

            // Install provider
            var provider = new FWPM_PROVIDER0();
            provider.displayData.name = "SecureWall";
            provider.displayData.description = "SecureWall Provider";
            // No serviceName: BFE loads a service-owned provider's persistent
            // objects only while that service is set to auto-start, so a Disabled
            // or Manual SecureWall service would silently drop the deny baseline
            // after reboot. Only explicit removal under this key releases it.
            provider.flags = FWPM_PROVIDER_FLAGS.FWPM_PROVIDER_FLAG_PERSISTENT;
            provider.providerKey = SECUREWALL_PROVIDER_KEY;
            var providerKey = baseline.RegisterProvider(ref provider);
            Debug.Assert(SECUREWALL_PROVIDER_KEY == providerKey);

            // Install sublayers
            var layerKeys = (LayerKeyEnum[])Enum.GetValues(typeof(LayerKeyEnum));
            foreach (var layer in layerKeys)
            {
                var slKey = GetSublayerKey(layer);
                using var wfpSublayer = new Sublayer($"SecureWall Sublayer for {layer}");
                wfpSublayer.Weight = ushort.MaxValue >> 4;
                wfpSublayer.SublayerKey = slKey;
                wfpSublayer.ProviderKey = SECUREWALL_PROVIDER_KEY;
                wfpSublayer.Flags = FWPM_SUBLAYER_FLAGS.FWPM_SUBLAYER_FLAG_PERSISTENT;
                baseline.RegisterSublayer(wfpSublayer);
            }

            foreach (LayerKeyEnum layer in layerKeys)
            {
                if (!LayerIsAleAuthConnect(layer) && !LayerIsAleAuthRecvAccept(layer) && !LayerIsIcmpError(layer))
                    continue;
                using var filter = new Filter("SecureWall recovery default deny", string.Empty,
                    SECUREWALL_PROVIDER_KEY, FilterActions.FWP_ACTION_BLOCK,
                    EnforcementPolicy.RecoveryBlockWeight((ulong)FilterWeights.DefaultBlock));
                filter.LayerKey = GetLayerKey(layer);
                filter.SublayerKey = GetSublayerKey(layer);
                filter.Conditions.Add(new FlagsFilterCondition(ConditionFlags.FWP_CONDITION_FLAG_IS_LOOPBACK,
                    FieldMatchType.FWP_MATCH_FLAGS_NONE_SET));
                WfpFilterPairRegistration.Register(lifetime =>
                {
                    filter.FilterKey = Guid.NewGuid();
                    filter.Flags = lifetime == WfpFilterLifetime.Persistent
                        ? FilterFlags.FWPM_FILTER_FLAG_PERSISTENT : FilterFlags.FWPM_FILTER_FLAG_BOOTTIME;
                    baseline.RegisterFilter(filter);
                    return filter.FilterId;
                }, true);
            }

            transaction.Commit();
            BaselineInstalled = true;
        }

        private enum LayerKeyEnum
        {
            FWPM_LAYER_OUTBOUND_ICMP_ERROR_V6,
            FWPM_LAYER_OUTBOUND_ICMP_ERROR_V4,
            FWPM_LAYER_INBOUND_ICMP_ERROR_V6,
            FWPM_LAYER_INBOUND_ICMP_ERROR_V4,
            FWPM_LAYER_ALE_AUTH_CONNECT_V6,
            FWPM_LAYER_ALE_AUTH_CONNECT_V4,
            FWPM_LAYER_ALE_AUTH_LISTEN_V6,
            FWPM_LAYER_ALE_AUTH_LISTEN_V4,
            FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6,
            FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4,
            FWPM_LAYER_INBOUND_TRANSPORT_V6_DISCARD,
            FWPM_LAYER_INBOUND_TRANSPORT_V4_DISCARD,
            FWPM_LAYER_ALE_RESOURCE_ASSIGNMENT_V6,
            FWPM_LAYER_ALE_RESOURCE_ASSIGNMENT_V4,
        }

        private static Guid GetSublayerKey(LayerKeyEnum layer)
        {
            return layer switch
            {
                LayerKeyEnum.FWPM_LAYER_OUTBOUND_ICMP_ERROR_V6 => WfpSublayerKeys.FWPM_LAYER_OUTBOUND_ICMP_ERROR_V6,
                LayerKeyEnum.FWPM_LAYER_OUTBOUND_ICMP_ERROR_V4 => WfpSublayerKeys.FWPM_LAYER_OUTBOUND_ICMP_ERROR_V4,
                LayerKeyEnum.FWPM_LAYER_INBOUND_ICMP_ERROR_V6 => WfpSublayerKeys.FWPM_LAYER_INBOUND_ICMP_ERROR_V6,
                LayerKeyEnum.FWPM_LAYER_INBOUND_ICMP_ERROR_V4 => WfpSublayerKeys.FWPM_LAYER_INBOUND_ICMP_ERROR_V4,
                LayerKeyEnum.FWPM_LAYER_ALE_AUTH_CONNECT_V6 => WfpSublayerKeys.FWPM_LAYER_ALE_AUTH_CONNECT_V6,
                LayerKeyEnum.FWPM_LAYER_ALE_AUTH_CONNECT_V4 => WfpSublayerKeys.FWPM_LAYER_ALE_AUTH_CONNECT_V4,
                LayerKeyEnum.FWPM_LAYER_ALE_AUTH_LISTEN_V6 => WfpSublayerKeys.FWPM_LAYER_ALE_AUTH_LISTEN_V6,
                LayerKeyEnum.FWPM_LAYER_ALE_AUTH_LISTEN_V4 => WfpSublayerKeys.FWPM_LAYER_ALE_AUTH_LISTEN_V4,
                LayerKeyEnum.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6 => WfpSublayerKeys.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6,
                LayerKeyEnum.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4 => WfpSublayerKeys.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4,
                LayerKeyEnum.FWPM_LAYER_INBOUND_TRANSPORT_V6_DISCARD => WfpSublayerKeys.FWPM_LAYER_INBOUND_TRANSPORT_V6_DISCARD,
                LayerKeyEnum.FWPM_LAYER_INBOUND_TRANSPORT_V4_DISCARD => WfpSublayerKeys.FWPM_LAYER_INBOUND_TRANSPORT_V4_DISCARD,
                LayerKeyEnum.FWPM_LAYER_ALE_RESOURCE_ASSIGNMENT_V6 => WfpSublayerKeys.FWPM_LAYER_ALE_RESOURCE_ASSIGNMENT_V6,
                LayerKeyEnum.FWPM_LAYER_ALE_RESOURCE_ASSIGNMENT_V4 => WfpSublayerKeys.FWPM_LAYER_ALE_RESOURCE_ASSIGNMENT_V4,
                _ => throw new ArgumentException("Invalid or not support layerEnum."),
            };
        }

        private static Guid GetLayerKey(LayerKeyEnum layer)
        {
            return layer switch
            {
                LayerKeyEnum.FWPM_LAYER_OUTBOUND_ICMP_ERROR_V6 => LayerKeys.FWPM_LAYER_OUTBOUND_ICMP_ERROR_V6,
                LayerKeyEnum.FWPM_LAYER_OUTBOUND_ICMP_ERROR_V4 => LayerKeys.FWPM_LAYER_OUTBOUND_ICMP_ERROR_V4,
                LayerKeyEnum.FWPM_LAYER_INBOUND_ICMP_ERROR_V6 => LayerKeys.FWPM_LAYER_INBOUND_ICMP_ERROR_V6,
                LayerKeyEnum.FWPM_LAYER_INBOUND_ICMP_ERROR_V4 => LayerKeys.FWPM_LAYER_INBOUND_ICMP_ERROR_V4,
                LayerKeyEnum.FWPM_LAYER_ALE_AUTH_CONNECT_V6 => LayerKeys.FWPM_LAYER_ALE_AUTH_CONNECT_V6,
                LayerKeyEnum.FWPM_LAYER_ALE_AUTH_CONNECT_V4 => LayerKeys.FWPM_LAYER_ALE_AUTH_CONNECT_V4,
                LayerKeyEnum.FWPM_LAYER_ALE_AUTH_LISTEN_V6 => LayerKeys.FWPM_LAYER_ALE_AUTH_LISTEN_V6,
                LayerKeyEnum.FWPM_LAYER_ALE_AUTH_LISTEN_V4 => LayerKeys.FWPM_LAYER_ALE_AUTH_LISTEN_V4,
                LayerKeyEnum.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6 => LayerKeys.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6,
                LayerKeyEnum.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4 => LayerKeys.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4,
                LayerKeyEnum.FWPM_LAYER_INBOUND_TRANSPORT_V6_DISCARD => LayerKeys.FWPM_LAYER_INBOUND_TRANSPORT_V6_DISCARD,
                LayerKeyEnum.FWPM_LAYER_INBOUND_TRANSPORT_V4_DISCARD => LayerKeys.FWPM_LAYER_INBOUND_TRANSPORT_V4_DISCARD,
                LayerKeyEnum.FWPM_LAYER_ALE_RESOURCE_ASSIGNMENT_V6 => LayerKeys.FWPM_LAYER_ALE_RESOURCE_ASSIGNMENT_V6,
                LayerKeyEnum.FWPM_LAYER_ALE_RESOURCE_ASSIGNMENT_V4 => LayerKeys.FWPM_LAYER_ALE_RESOURCE_ASSIGNMENT_V4,
                _ => throw new ArgumentException("Invalid or not support layerEnum."),
            };
        }

        private IReadOnlyList<ulong> InstallWfpFilter(Filter f, FilterGroup group, bool required = true)
        {
            IReadOnlyList<ulong> filterIds = WfpFilterPairRegistration.Register(lifetime =>
            {
                f.FilterKey = Guid.NewGuid();
                f.Flags = 0;
                WfpEngine.RegisterFilter(f);
                (PendingRuntimeFilterKeys ?? throw new InvalidOperationException("No runtime transaction is active.")).Add(f.FilterKey);
                return f.FilterId;
            }, required, runtimeOnly: true);
            if (group != FilterGroup.Unknown)
            {
                try
                {
                    if (PendingFilterGroups != null)
                    {
                        if (filterIds.Count > FilterGroupMap.Capacity - PendingFilterGroups.Count)
                            PendingFilterGroups = null;
                        else foreach (ulong id in filterIds) PendingFilterGroups.Add(new KeyValuePair<ulong, FilterGroup>(id, group));
                    }
                }
                catch { PendingFilterGroups = null; } // Display data cannot reject a rule.
            }
            return filterIds;
        }

        private void ConstructFilter(RuleDef r, LayerKeyEnum layer, List<ulong> promptableFilterIds)
        {
            // Validate the whole list before allocating native conditions or considering
            // layer-specific skips. A malformed explicit block must abort replacement.
            EnforcementPolicy.ValidateRemoteAddresses(r.RemoteAddresses);
            // Local helper methods

            bool addCommonIpFilterCondition(IpFilterCondition cond, FilterConditionList coll)
            {
                if (cond.IsIPv6 == LayerIsV6Stack(layer))
                {
                    coll.Add(cond);
                    return true;
                }
                return false;
            }
            bool addIpFilterCondition(IpAddrMask peerAddr, RemoteOrLocal peerType, FilterConditionList coll)
            {
                if (peerAddr.IsIPv6 == LayerIsV6Stack(layer))
                {
                    coll.Add(new IpFilterCondition(peerAddr.Address, (byte)peerAddr.PrefixLen, peerType));
                    return true;
                }
                return false;
            }
            (ushort, ushort) parseUInt16Range(ReadOnlySpan<char> str)
            {
                if (-1 != str.IndexOf('-'))
                {
                    ReadOnlySpan<char> min, max;
                    using (var enumerator = str.Split('-'))
                    {
                        enumerator.MoveNext(); min = enumerator.Current;
                        enumerator.MoveNext(); max = enumerator.Current;
                    }
                    return (min.DecimalToUInt16(), max.DecimalToUInt16());
                }
                else
                {
                    var port = str.DecimalToUInt16();
                    return (port, port);
                }
            }

            // ---------------------------------------

            using var conditions = new FilterConditionList();

            // Application identity
            if (!Utils.IsNullOrEmpty(r.AppContainerSid))
            {
                System.Diagnostics.Debug.Assert(!r.AppContainerSid.Equals("*"));

                // Skip filter if OS is not supported
                if (!pylorak.Windows.VersionInfo.Win81OrNewer)
                    return;

                if (!LayerIsIcmpError(layer))
                    conditions.Add(new PackageIdFilterCondition(r.AppContainerSid));
                else
                    return;
            }
            else
            {
                if (!Utils.IsNullOrEmpty(r.ServiceName))
                {
                    System.Diagnostics.Debug.Assert(!r.ServiceName.Equals("*"));
                    if (!LayerIsIcmpError(layer))
                        conditions.Add(new ServiceNameFilterCondition(r.ServiceName));
                    else
                        return;
                }

                if (!Utils.IsNullOrEmpty(r.Application))
                {
                    System.Diagnostics.Debug.Assert(!r.Application.Equals("*"));

                    if (!LayerIsIcmpError(layer))
                        conditions.Add(new AppIdFilterCondition(r.Application, false, true));
                    else
                        return;
                }
            }

            // IP address
            if (!Utils.IsNullOrEmpty(r.RemoteAddresses))
            {
                System.Diagnostics.Debug.Assert(!r.RemoteAddresses.Equals("*"));

                bool validAddressFound = false;
                foreach (var ipStr in r.RemoteAddresses.AsSpan().Split(',', SpanSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        if (ipStr.Equals(RuleDef.LOCALSUBNET_ID, StringComparison.Ordinal))
                        {
                            foreach (var filter in LocalSubnetFilterConditions)
                                validAddressFound |= addCommonIpFilterCondition((IpFilterCondition)filter, conditions);
                        }
                        else if (ipStr.Equals("DefaultGateway", StringComparison.Ordinal))
                        {
                            foreach (var filter in GatewayFilterConditions)
                                validAddressFound |= addCommonIpFilterCondition((IpFilterCondition)filter, conditions);
                        }
                        else if (ipStr.Equals("DNS", StringComparison.Ordinal))
                        {
                            foreach (var filter in DnsFilterConditions)
                                validAddressFound |= addCommonIpFilterCondition((IpFilterCondition)filter, conditions);
                        }
                        else
                        {
                            validAddressFound |= addIpFilterCondition(IpAddrMask.Parse(ipStr), RemoteOrLocal.Remote, conditions);
                        }
                    }
                    catch (Exception error)
                    {
                        throw new InvalidOperationException("Failed to construct a required remote address condition.", error);
                    }
                }

                if (!validAddressFound)
                {
                    // Break. We don't want to add this filter to this layer.
                    return;
                }
            }

            // We never want to affect loopback traffic
            conditions.Add(new FlagsFilterCondition(ConditionFlags.FWP_CONDITION_FLAG_IS_LOOPBACK, FieldMatchType.FWP_MATCH_FLAGS_NONE_SET));

            // Protocol
            if (r.Protocol != Protocol.Any)
            {
                if (LayerIsAleAuthConnect(layer) || LayerIsAleAuthRecvAccept(layer))
                {
                    if (r.Protocol == Protocol.TcpUdp)
                    {
                        conditions.Add(new ProtocolFilterCondition((byte)Protocol.TCP));
                        conditions.Add(new ProtocolFilterCondition((byte)Protocol.UDP));
                    }
                    else
                        conditions.Add(new ProtocolFilterCondition((byte)r.Protocol));
                }
            }

            // Ports
            if (!Utils.IsNullOrEmpty(r.LocalPorts))
            {
                System.Diagnostics.Debug.Assert(!r.LocalPorts.Equals("*"));
                foreach (var p in r.LocalPorts.AsSpan().Split(',', SpanSplitOptions.RemoveEmptyEntries))
                {
                    (var minPort, var maxPort) = parseUInt16Range(p);
                    conditions.Add(new PortFilterCondition(minPort, maxPort, RemoteOrLocal.Local));
                }
            }
            if (!Utils.IsNullOrEmpty(r.RemotePorts))
            {
                System.Diagnostics.Debug.Assert(!r.RemotePorts.Equals("*"));
                foreach (var p in r.RemotePorts.AsSpan().Split(',', SpanSplitOptions.RemoveEmptyEntries))
                {
                    (var minPort, var maxPort) = parseUInt16Range(p);
                    conditions.Add(new PortFilterCondition(minPort, maxPort, RemoteOrLocal.Remote));
                }
            }

            // ICMP
            if (!Utils.IsNullOrEmpty(r.IcmpTypesAndCodes))
            {
                System.Diagnostics.Debug.Assert(!r.IcmpTypesAndCodes.Equals("*"));
                foreach (var e in r.IcmpTypesAndCodes.AsSpan().Split(',', SpanSplitOptions.RemoveEmptyEntries))
                {
                    using var tc = e.Split(':');
                    tc.MoveNext(); var icmpType = tc.Current;

                    if (LayerIsIcmpError(layer))
                    {
                        // ICMP Type
                        if ((icmpType.Length != 0) && icmpType.TryDecimalToUInt16(out ushort icmpTypeVal))
                            conditions.Add(new IcmpErrorTypeFilterCondition(icmpTypeVal));

                        // ICMP Code
                        if (tc.MoveNext())
                        {
                            var icmpCode = tc.Current;
                            if ((icmpCode.Length != 0) && !icmpCode.Equals("*", StringComparison.Ordinal) && icmpCode.TryDecimalToUInt16(out ushort icmpCodeVal))
                                conditions.Add(new IcmpErrorCodeFilterCondition(icmpCodeVal));
                        }
                    }
                    else
                    {
                        // ICMP Type - note different condition key
                        if ((icmpType.Length != 0) && icmpType.TryDecimalToUInt16(out ushort icmpTypeVal))
                            conditions.Add(new IcmpTypeFilterCondition(icmpTypeVal));

                        // Matching on ICMP Code not possible
                    }
                }
            }

            // Create and install filter
            using var f = new Filter(
                r.ExceptionId.ToString(),
                r.Name,
                SECUREWALL_PROVIDER_KEY,
                (r.Action == RuleAction.Allow) ? FilterActions.FWP_ACTION_PERMIT : FilterActions.FWP_ACTION_BLOCK,
                r.Weight,
                conditions
            );
            f.LayerKey = GetLayerKey(layer);
            f.SublayerKey = GetSublayerKey(layer);

            IReadOnlyList<ulong> installedFilterIds = InstallWfpFilter(
                f,
                r.Action == RuleAction.Block
                    ? FilterGroupClassifier.ForBlockRule(r.Weight, (ulong)FilterWeights.Blocklist,
                        (ulong)FilterWeights.UserBlock, (ulong)FilterWeights.DefaultBlock)
                    : FilterGroup.Unknown,
                PromptFilterClassifier.IsRequiredProtection(r.Action == RuleAction.Block));
            if (r.Action == RuleAction.Block && r.Weight == (ulong)FilterWeights.Blocklist)
            {
                try
                {
                    if (PendingPortBlocklistFilterIds != null)
                    {
                        if (installedFilterIds.Count > DiagnosticFilterSet.Capacity - PendingPortBlocklistFilterIds.Count)
                            PendingPortBlocklistFilterIds = null;
                        else PendingPortBlocklistFilterIds.AddRange(installedFilterIds);
                    }
                }
                catch { PendingPortBlocklistFilterIds = null; } // No diagnostic allocation can reject a rule.
            }
            if (PromptFilterClassifier.IsPromptable(
                r.Action == RuleAction.Block,
                r.Weight,
                (ulong)FilterWeights.DefaultBlock,
                LayerIsAleAuthConnect(layer)))
            {
                promptableFilterIds.AddRange(installedFilterIds);
            }
        }

        private void InstallRawSocketBlocks()
        {
            InstallRawSocketBlocks(LayerKeyEnum.FWPM_LAYER_ALE_RESOURCE_ASSIGNMENT_V4);
            InstallRawSocketBlocks(LayerKeyEnum.FWPM_LAYER_ALE_RESOURCE_ASSIGNMENT_V6);
        }

        private void InstallRawSocketBlocks(LayerKeyEnum layer)
        {
            using var f = new Filter(
                "Raw socket block",
                string.Empty,
                SECUREWALL_PROVIDER_KEY,
                FilterActions.FWP_ACTION_BLOCK,
                (ulong)FilterWeights.RawSocketBlock
            );
            f.LayerKey = GetLayerKey(layer);
            f.SublayerKey = GetSublayerKey(layer);
            f.Conditions.Add(new FlagsFilterCondition(ConditionFlags.FWP_CONDITION_FLAG_IS_RAW_ENDPOINT, FieldMatchType.FWP_MATCH_FLAGS_ANY_SET));

            InstallWfpFilter(f, FilterGroup.RawSocket);
        }

        private static readonly LayerKeyEnum[] Wsl2Layers =
        {
            LayerKeyEnum.FWPM_LAYER_ALE_AUTH_CONNECT_V4,
            LayerKeyEnum.FWPM_LAYER_ALE_AUTH_CONNECT_V6,
            LayerKeyEnum.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4,
            LayerKeyEnum.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6,
            LayerKeyEnum.FWPM_LAYER_OUTBOUND_ICMP_ERROR_V4,
            LayerKeyEnum.FWPM_LAYER_OUTBOUND_ICMP_ERROR_V6,
            LayerKeyEnum.FWPM_LAYER_INBOUND_ICMP_ERROR_V4,
            LayerKeyEnum.FWPM_LAYER_INBOUND_ICMP_ERROR_V6,
        };

        private string LastWsl2Report = string.Empty;

        // Called once per full reload. Permits are optional and never abort the policy
        // transaction; block registration failures still do (see Wsl2AdapterPolicy).
        private void InstallWsl2Filters(bool permit)
        {
            var report = new List<string>();
            var adapters = new List<(string Alias, ulong Luid)>();
            try
            {
                foreach (var entry in LocalInterfaceCondition.EnumerateInterfaces())
                {
                    Wsl2AdapterKind kind = Wsl2AdapterPolicy.Classify(entry.Alias);
                    if (kind == Wsl2AdapterKind.Wsl)
                        adapters.Add(entry);
                    else if (kind == Wsl2AdapterKind.UnrecognizedWsl)
                        report.Add("Adapter '" + entry.Alias + "' looks like WSL but is not a known WSL 2 NAT adapter; no WSL filters apply to it.");
                }
            }
            catch (Exception exception) when (permit)
            {
                report.Add("WSL 2 adapter discovery failed (0x" + exception.HResult.ToString("X8") + "); the WSL_2 permit was not installed.");
                adapters.Clear();
            }

            Wsl2AdapterPolicy.Install(adapters, permit,
                adapter => { foreach (LayerKeyEnum layer in Wsl2Layers) InstallWsl2Filters(permit, adapter.Luid, layer); },
                (adapter, exception) => report.Add("Could not install the WSL 2 permit for adapter '" + adapter.Alias + "' (0x" + exception.HResult.ToString("X8") + "); WSL traffic on it stays under the remaining policy."));
            if (permit && adapters.Count == 0 && report.Count == 0)
                report.Add("The WSL_2 exception is enabled but no WSL 2 NAT adapter was found. WSL may be stopped or using mirrored networking; no WSL permit was installed.");

            // Reloads are frequent; log only when the outcome changes.
            string summary = string.Join(Environment.NewLine, report);
            if (summary != LastWsl2Report)
            {
                LastWsl2Report = summary;
                if (summary.Length != 0)
                    Utils.Log(summary, Utils.LOG_ID_SERVICE);
            }
        }

        private void InstallWsl2Filters(bool permit, ulong interfaceLuid, LayerKeyEnum layer)
        {
            FilterActions action = permit ? FilterActions.FWP_ACTION_PERMIT : FilterActions.FWP_ACTION_BLOCK;
            ulong weight = (ulong)(permit ? FilterWeights.UserPermit : FilterWeights.UserBlock);

            using var f = new Filter(
                "Allow WSL2",
                string.Empty,
                SECUREWALL_PROVIDER_KEY,
                action,
                weight
            );
            f.LayerKey = GetLayerKey(layer);
            f.SublayerKey = GetSublayerKey(layer);
            f.Conditions.Add(new LocalInterfaceCondition(interfaceLuid));

            InstallWfpFilter(f, permit ? FilterGroup.Unknown : FilterGroup.User);
        }

        // Optional AI assistant egress, full reloads only. SecureWall.exe is also the LocalSystem
        // service image, so the permit carries a user condition that denies the service
        // accounts and allows only interactive tokens, plus outbound TCP to remote port 443.
        // It sits above user blocks (see AiExplainEgressPolicy.PermitWeight); blocklists still win.
        private void InstallAiAssistantPermit()
        {
            bool displayOffBlockActive = PolicyConfiguration.ActiveProfile.DisplayOffBlock && !DisplayCurrentlyOn;
            if (!AiExplainEgressPolicy.ShouldInstallPermit(PolicyConfiguration.AiAssistantEgress,
                PolicyMode == FirewallMode.Normal, displayOffBlockActive))
                return;

            string image = PathMapper.Instance.ConvertPath(Utils.ExecutablePath.AsSpan(), PathFormat.NativeNt);
            InstallAiAssistantPermit(image, LayerKeyEnum.FWPM_LAYER_ALE_AUTH_CONNECT_V4);
            InstallAiAssistantPermit(image, LayerKeyEnum.FWPM_LAYER_ALE_AUTH_CONNECT_V6);
        }

        private void InstallAiAssistantPermit(string ntImagePath, LayerKeyEnum layer)
        {
            using var f = new Filter(
                "SecureWall AI assistant",
                "Outbound TCP 443 for the interactive SecureWall controller only",
                SECUREWALL_PROVIDER_KEY,
                FilterActions.FWP_ACTION_PERMIT,
                (ulong)FilterWeights.OwnImagePermit
            );
            f.LayerKey = GetLayerKey(layer);
            f.SublayerKey = GetSublayerKey(layer);
            f.Conditions.Add(new AppIdFilterCondition(ntImagePath, false, true));
            f.Conditions.Add(new UserIdFilterCondition(AiExplainEgressPolicy.AllowedUserSids,
                AiExplainEgressPolicy.DeniedUserSids, RemoteOrLocal.Local));
            f.Conditions.Add(new FlagsFilterCondition(ConditionFlags.FWP_CONDITION_FLAG_IS_LOOPBACK, FieldMatchType.FWP_MATCH_FLAGS_NONE_SET));
            f.Conditions.Add(new ProtocolFilterCondition(AiExplainEgressPolicy.TcpProtocol));
            f.Conditions.Add(new PortFilterCondition(AiExplainEgressPolicy.RemotePort, AiExplainEgressPolicy.RemotePort, RemoteOrLocal.Remote));

            // Like every rule, a rejected permit fails the whole replacement (fail closed).
            // Permits never drop traffic, so they carry no block-reason group.
            InstallWfpFilter(f, FilterGroup.Unknown);
        }

        // Check for updates: one permit per active lease (UpdateNetworkGrantPolicy). Shaped like
        // the AI assistant's permit, except that the allowed user is the requesting user's
        // SID only, so another signed-in user's SecureWall.exe cannot use it.
        private void InstallUpdatePermit(UpdateNetworkLease lease)
        {
            string image = PathMapper.Instance.ConvertPath(Utils.ExecutablePath.AsSpan(), PathFormat.NativeNt);
            InstallUpdatePermit(lease, image, LayerKeyEnum.FWPM_LAYER_ALE_AUTH_CONNECT_V4);
            InstallUpdatePermit(lease, image, LayerKeyEnum.FWPM_LAYER_ALE_AUTH_CONNECT_V6);
        }

        private void InstallUpdatePermit(UpdateNetworkLease lease, string ntImagePath, LayerKeyEnum layer)
        {
            using var f = new Filter(
                "SecureWall update check",
                "Outbound TCP 443 for one user's SecureWall controller while it checks for updates",
                SECUREWALL_PROVIDER_KEY,
                FilterActions.FWP_ACTION_PERMIT,
                (ulong)FilterWeights.OwnImagePermit
            );
            f.LayerKey = GetLayerKey(layer);
            f.SublayerKey = GetSublayerKey(layer);
            f.Conditions.Add(new AppIdFilterCondition(ntImagePath, false, true));
            f.Conditions.Add(new UserIdFilterCondition(new[] { lease.UserSid },
                AiExplainEgressPolicy.DeniedUserSids, RemoteOrLocal.Local));
            f.Conditions.Add(new FlagsFilterCondition(ConditionFlags.FWP_CONDITION_FLAG_IS_LOOPBACK, FieldMatchType.FWP_MATCH_FLAGS_NONE_SET));
            f.Conditions.Add(new ProtocolFilterCondition(AiExplainEgressPolicy.TcpProtocol));
            f.Conditions.Add(new PortFilterCondition(AiExplainEgressPolicy.RemotePort, AiExplainEgressPolicy.RemotePort, RemoteOrLocal.Remote));
            InstallWfpFilter(f, FilterGroup.Unknown);
        }

        // Full reloads re-add the permits of leases still valid; the caller assigns the keys
        // once the reload commits.
        private List<KeyValuePair<UpdateNetworkLease, List<Guid>>> InstallUpdatePermits()
        {
            var installed = new List<KeyValuePair<UpdateNetworkLease, List<Guid>>>();
            bool displayOffBlockActive = PolicyConfiguration.ActiveProfile.DisplayOffBlock && !DisplayCurrentlyOn;
            if (!UpdateNetworkGrantPolicy.ShouldInstall(PolicyMode == FirewallMode.Normal, displayOffBlockActive))
                return installed;
            List<Guid> pending = PendingRuntimeFilterKeys ?? throw new InvalidOperationException("No runtime transaction is active.");
            foreach (UpdateNetworkLease lease in UpdateLeases.Active)
            {
                if (UpdateLeases.Remaining(lease) <= TimeSpan.Zero || (lease.Process as UpdateNetworkClient)?.HasExited != false)
                    continue;
                int first = pending.Count;
                InstallUpdatePermit(lease);
                installed.Add(new KeyValuePair<UpdateNetworkLease, List<Guid>>(lease, pending.GetRange(first, pending.Count - first)));
            }
            return installed;
        }

        // After a committed full reload: leases whose permit was re-added get its new keys;
        // every other lease (expired, exited, or a mode or display state without permits) ends.
        // A lease that expired while the reload committed has new filters, which are removed here.
        private void CommitUpdatePermits(List<KeyValuePair<UpdateNetworkLease, List<Guid>>> installed)
        {
            foreach (var pair in installed)
                pair.Key.FilterKeys = pair.Value;
            List<UpdateNetworkLease> ended = UpdateLeases.RemoveDue(lease => !installed.Exists(pair => ReferenceEquals(pair.Key, lease)));
            foreach (UpdateNetworkLease lease in ended)
            {
                if (!installed.Exists(pair => ReferenceEquals(pair.Key, lease)))
                    lease.FilterKeys.Clear(); // The reload already removed them.
            }
            RemoveUpdateLeases(ended);
        }

        // Enforcement thread only.
        private UpdateGrantStatus GrantUpdateNetwork(uint clientProcessId, out int secondsLeft)
        {
            secondsLeft = 0;
            bool displayOffBlockActive = ActiveConfig.Service.ActiveProfile.DisplayOffBlock && !DisplayCurrentlyOn;
            UpdateGrantStatus status = UpdateNetworkGrantPolicy.Decide(VisibleState.Mode, displayOffBlockActive, RuntimeSessionRevoked);
            if (status != UpdateGrantStatus.Granted)
                return status;

            UpdateNetworkClient? client = UpdateNetworkClient.TryOpen(clientProcessId, Utils.ExecutablePath, out string reason);
            if (client == null)
            {
                Utils.Log("Refused an update-check network permit: " + reason + ".", Utils.LOG_ID_SERVICE);
                return UpdateGrantStatus.Refused;
            }

            UpdateNetworkLease lease = UpdateLeases.Grant(client.ProcessId, client.CreationTime, client.UserSid, client,
                out List<UpdateNetworkLease> replaced);
            RemoveUpdateLeases(replaced);
            var keys = new List<Guid>();
            try
            {
                using Transaction trx = WfpEngine.BeginTransaction();
                PendingRuntimeFilterKeys = keys;
                InstallUpdatePermit(lease);
                trx.Commit();
                RuntimeFilterKeys.AddRange(keys);
                lease.FilterKeys = keys;
            }
            catch (Exception exception)
            {
                Utils.LogException(exception, Utils.LOG_ID_SERVICE);
                RemoveUpdateLeases(UpdateLeases.Release(lease.ProcessId, lease.CreationTime));
                return UpdateGrantStatus.Failed;
            }
            finally
            {
                PendingRuntimeFilterKeys = null;
            }

            TimeSpan remaining = UpdateLeases.Remaining(lease);
            client.Watch(remaining, PostUpdateLeaseReview);
            secondsLeft = (int)Math.Ceiling(remaining.TotalSeconds);
            return UpdateGrantStatus.Granted;
        }

        private void ReleaseUpdateNetwork(uint clientProcessId)
        {
            foreach (UpdateNetworkLease lease in UpdateLeases.Active)
            {
                // The lease holds the process handle, so this process ID still names its process.
                if (lease.ProcessId == clientProcessId)
                {
                    RemoveUpdateLeases(UpdateLeases.Release(lease.ProcessId, lease.CreationTime));
                    return;
                }
            }
        }

        // Ends expired leases and leases whose process exited, and re-arms the watch of the rest
        // (a wait can fire early, and a watch fires only once).
        private void ReviewUpdateLeases()
        {
            RemoveUpdateLeases(UpdateLeases.RemoveDue(lease => (lease.Process as UpdateNetworkClient)?.HasExited != false));
            foreach (UpdateNetworkLease lease in UpdateLeases.Active)
                (lease.Process as UpdateNetworkClient)?.Watch(UpdateLeases.Remaining(lease), PostUpdateLeaseReview);
        }

        private void PostUpdateLeaseReview()
        {
            try
            {
                if (!RuntimeStopping)
                    Q.Add(new TwRequest(TwMessageSimple.CreateRequest(MessageType.UPDATE_NETWORK_REVIEW)));
            }
            catch (ObjectDisposedException) { } // The service is stopping; the session closes anyway.
            catch (InvalidOperationException) { }
        }

        // Removes the leases' permits in one transaction. A permit that cannot be removed must
        // not outlive its lease, so a failure withdraws every runtime grant (fail closed).
        private void RemoveUpdateLeases(List<UpdateNetworkLease> leases)
        {
            var keys = new List<Guid>();
            foreach (UpdateNetworkLease lease in leases)
            {
                keys.AddRange(lease.FilterKeys);
                lease.FilterKeys = new List<Guid>();
                lease.Process?.Dispose();
            }
            if (keys.Count == 0 || RuntimeSessionRevoked)
                return;
            try
            {
                using (Transaction trx = WfpEngine.BeginTransaction())
                {
                    foreach (Guid key in keys)
                        WfpEngine.UnregisterFilter(key);
                    trx.Commit();
                }
                RuntimeFilterKeys.RemoveAll(keys.Contains);
            }
            catch (Exception exception)
            {
                Utils.Log("Could not remove an update-check network permit; withdrawing runtime permissions.", Utils.LOG_ID_SERVICE);
                Utils.LogException(exception, Utils.LOG_ID_SERVICE);
                FailClosed();
            }
        }

        private void InstallRawSocketPermits(List<RuleDef> rawSocketExceptions)
        {
            InstallRawSocketPermits(rawSocketExceptions, LayerKeyEnum.FWPM_LAYER_ALE_RESOURCE_ASSIGNMENT_V4);
            InstallRawSocketPermits(rawSocketExceptions, LayerKeyEnum.FWPM_LAYER_ALE_RESOURCE_ASSIGNMENT_V6);
        }

        private void InstallRawSocketPermits(List<RuleDef> rawSocketExceptions, LayerKeyEnum layer)
        {
            foreach (var subj in rawSocketExceptions)
            {
                using var conditions = new FilterConditionList();
                if (!Utils.IsNullOrEmpty(subj.Application))
                    conditions.Add(new AppIdFilterCondition(subj.Application, false, true));
                if (!Utils.IsNullOrEmpty(subj.ServiceName))
                    conditions.Add(new ServiceNameFilterCondition(subj.ServiceName));
                if (conditions.Count == 0)
                    continue;

                using var f = new Filter(
                    "Raw socket permit",
                    string.Empty,
                    SECUREWALL_PROVIDER_KEY,
                    FilterActions.FWP_ACTION_PERMIT,
                    (ulong)FilterWeights.RawSocketPermit,
                    conditions
                );
                f.LayerKey = GetLayerKey(layer);
                f.SublayerKey = GetSublayerKey(layer);

                InstallWfpFilter(f, FilterGroup.Unknown);
            }
        }

        private void InstallPortScanProtection()
        {
            InstallPortScanProtection(LayerKeyEnum.FWPM_LAYER_INBOUND_TRANSPORT_V4_DISCARD, BuiltinCallouts.FWPM_CALLOUT_WFP_TRANSPORT_LAYER_V4_SILENT_DROP);
            InstallPortScanProtection(LayerKeyEnum.FWPM_LAYER_INBOUND_TRANSPORT_V6_DISCARD, BuiltinCallouts.FWPM_CALLOUT_WFP_TRANSPORT_LAYER_V6_SILENT_DROP);
        }

        private void InstallPortScanProtection(LayerKeyEnum layer, Guid callout)
        {
            using var f = new Filter(
                "Port Scanning Protection",
                string.Empty,
                SECUREWALL_PROVIDER_KEY,
                FilterActions.FWP_ACTION_CALLOUT_TERMINATING,
                (ulong)FilterWeights.Blocklist
            );
            f.LayerKey = GetLayerKey(layer);
            f.SublayerKey = GetSublayerKey(layer);
            f.CalloutKey = callout;

            // Don't affect loopback traffic
            f.Conditions.Add(new FlagsFilterCondition(ConditionFlags.FWP_CONDITION_FLAG_IS_LOOPBACK | ConditionFlags.FWP_CONDITION_FLAG_IS_IPSEC_SECURED, FieldMatchType.FWP_MATCH_FLAGS_NONE_SET));

            InstallWfpFilter(f, FilterGroup.PortScan);
        }

        private static bool LayerIsAleAuthConnect(LayerKeyEnum layer)
        {
            return
                (layer == LayerKeyEnum.FWPM_LAYER_ALE_AUTH_CONNECT_V4) ||
                (layer == LayerKeyEnum.FWPM_LAYER_ALE_AUTH_CONNECT_V6);
        }

        private static bool LayerIsAleAuthRecvAccept(LayerKeyEnum layer)
        {
            return
                (layer == LayerKeyEnum.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6) ||
                (layer == LayerKeyEnum.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4);
        }

        private static bool LayerIsIcmpError(LayerKeyEnum layer)
        {
            return
                (layer == LayerKeyEnum.FWPM_LAYER_OUTBOUND_ICMP_ERROR_V6) ||
                (layer == LayerKeyEnum.FWPM_LAYER_OUTBOUND_ICMP_ERROR_V4) ||
                (layer == LayerKeyEnum.FWPM_LAYER_INBOUND_ICMP_ERROR_V6) ||
                (layer == LayerKeyEnum.FWPM_LAYER_INBOUND_ICMP_ERROR_V4);
        }

        private static bool LayerIsV6Stack(LayerKeyEnum layer)
        {
            return
                (layer == LayerKeyEnum.FWPM_LAYER_ALE_AUTH_CONNECT_V6) ||
                (layer == LayerKeyEnum.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6) ||
                (layer == LayerKeyEnum.FWPM_LAYER_ALE_AUTH_LISTEN_V6) ||
                (layer == LayerKeyEnum.FWPM_LAYER_OUTBOUND_ICMP_ERROR_V6) ||
                (layer == LayerKeyEnum.FWPM_LAYER_INBOUND_ICMP_ERROR_V6);
        }

        private void ConstructFilter(RuleDef r, List<ulong> promptableFilterIds)
        {
            // Also, relevant info:
            // https://networkengineering.stackexchange.com/questions/58903/how-to-handle-icmp-in-ipv6-or-icmpv6-in-ipv4

            if ((r.Direction & RuleDirection.Out) != 0)
            {
                ConstructFilter(r, LayerKeyEnum.FWPM_LAYER_ALE_AUTH_CONNECT_V6, promptableFilterIds);
                ConstructFilter(r, LayerKeyEnum.FWPM_LAYER_ALE_AUTH_CONNECT_V4, promptableFilterIds);

                if ((r.Protocol == Protocol.Any) || (r.Protocol == Protocol.ICMPv6))
                    ConstructFilter(r, LayerKeyEnum.FWPM_LAYER_OUTBOUND_ICMP_ERROR_V6, promptableFilterIds);
                if ((r.Protocol == Protocol.Any) || (r.Protocol == Protocol.ICMPv4))
                    ConstructFilter(r, LayerKeyEnum.FWPM_LAYER_OUTBOUND_ICMP_ERROR_V4, promptableFilterIds);
            }
            if ((r.Direction & RuleDirection.In) != 0)
            {
                ConstructFilter(r, LayerKeyEnum.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6, promptableFilterIds);
                ConstructFilter(r, LayerKeyEnum.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4, promptableFilterIds);

                if ((r.Protocol == Protocol.Any) || (r.Protocol == Protocol.ICMPv6))
                    ConstructFilter(r, LayerKeyEnum.FWPM_LAYER_INBOUND_ICMP_ERROR_V6, promptableFilterIds);
                if ((r.Protocol == Protocol.Any) || (r.Protocol == Protocol.ICMPv4))
                    ConstructFilter(r, LayerKeyEnum.FWPM_LAYER_INBOUND_ICMP_ERROR_V4, promptableFilterIds);
            }
        }

        private static List<FirewallExceptionV3> CollectExceptionsForAppByName(string name)
        {
            var exceptions = new List<FirewallExceptionV3>();

            try
            {
                // Retrieve database entry for appName
                DatabaseClasses.Application? app = GlobalInstances.AppDatabase.GetApplicationByName(name);
                if (app is null)
                    return exceptions;

                // Create rules
                foreach (DatabaseClasses.SubjectIdentity id in app.Components)
                {
                    try
                    {
                        List<ExceptionSubject> foundSubjects = id.SearchForFile();
                        foreach (var subject in foundSubjects)
                        {
                            exceptions.Add(id.InstantiateException(subject));
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return exceptions;
        }

        private static void GetRulesForException(FirewallExceptionV3 ex, List<RuleDef> results, List<RuleDef> rawSocketExceptions, ulong permitWeight, ulong blockWeight)
        {
            if (EnforcementPolicy.IsExpired(ex.CreationDate, (int)ex.Timer, DateTime.Now))
                return;
            if (ex.Id == Guid.Empty)
            {
                // Do not let the service crash if a rule cannot be constructed
#if DEBUG
                throw new InvalidOperationException("Firewall exception specification must have an ID.");
#else
                ex.RegenerateId();
                GlobalInstances.ServerChangeset = Guid.NewGuid();
#endif
            }

            switch (ex.Policy.PolicyType)
            {
                case PolicyType.HardBlock:
                    {
                        var def = new RuleDef(ex.Id, "Block", ex.Subject, RuleAction.Block, RuleDirection.InOut, Protocol.Any, blockWeight);
                        results.Add(def);
                        break;
                    }
                case PolicyType.Unrestricted:
                    {
                        var pol = (UnrestrictedPolicy)ex.Policy;

                        var def = new RuleDef(ex.Id, "Full access", ex.Subject, RuleAction.Allow, RuleDirection.InOut, Protocol.Any, permitWeight);
                        if (pol.LocalNetworkOnly)
                            def.RemoteAddresses = RuleDef.LOCALSUBNET_ID;
                        results.Add(def);

                        // Make exception for promiscuous mode
                        rawSocketExceptions?.Add(def);

                        break;
                    }
                case PolicyType.TcpUdpOnly:
                    {
                        var pol = (TcpUdpPolicy)ex.Policy;

                        // Incoming
                        if (!string.IsNullOrEmpty(pol.AllowedLocalTcpListenerPorts) && (pol.AllowedLocalTcpListenerPorts == pol.AllowedLocalUdpListenerPorts))
                        {
                            var def = new RuleDef(ex.Id, "TCP/UDP Listen Ports", ex.Subject, RuleAction.Allow, RuleDirection.In, Protocol.TcpUdp, permitWeight);
                            if (!string.Equals(pol.AllowedLocalTcpListenerPorts, "*"))
                                def.LocalPorts = pol.AllowedLocalTcpListenerPorts;
                            if (pol.LocalNetworkOnly)
                                def.RemoteAddresses = RuleDef.LOCALSUBNET_ID;
                            results.Add(def);
                        }
                        else
                        {
                            if (!string.IsNullOrEmpty(pol.AllowedLocalTcpListenerPorts))
                            {
                                var def = new RuleDef(ex.Id, "TCP Listen Ports", ex.Subject, RuleAction.Allow, RuleDirection.In, Protocol.TCP, permitWeight);
                                if (!string.Equals(pol.AllowedLocalTcpListenerPorts, "*"))
                                    def.LocalPorts = pol.AllowedLocalTcpListenerPorts;
                                if (pol.LocalNetworkOnly)
                                    def.RemoteAddresses = RuleDef.LOCALSUBNET_ID;
                                results.Add(def);
                            }
                            if (!string.IsNullOrEmpty(pol.AllowedLocalUdpListenerPorts))
                            {
                                var def = new RuleDef(ex.Id, "UDP Listen Ports", ex.Subject, RuleAction.Allow, RuleDirection.In, Protocol.UDP, permitWeight);
                                if (!string.Equals(pol.AllowedLocalUdpListenerPorts, "*"))
                                    def.LocalPorts = pol.AllowedLocalUdpListenerPorts;
                                if (pol.LocalNetworkOnly)
                                    def.RemoteAddresses = RuleDef.LOCALSUBNET_ID;
                                results.Add(def);
                            }
                        }

                        // Outgoing
                        if (!string.IsNullOrEmpty(pol.AllowedRemoteTcpConnectPorts) && (pol.AllowedRemoteTcpConnectPorts == pol.AllowedRemoteUdpConnectPorts))
                        {
                            var def = new RuleDef(ex.Id, "TCP/UDP Outbound Ports", ex.Subject, RuleAction.Allow, RuleDirection.Out, Protocol.TcpUdp, permitWeight);
                            if (!string.Equals(pol.AllowedRemoteTcpConnectPorts, "*"))
                                def.RemotePorts = pol.AllowedRemoteTcpConnectPorts;
                            if (pol.LocalNetworkOnly)
                                def.RemoteAddresses = RuleDef.LOCALSUBNET_ID;
                            results.Add(def);
                        }
                        else
                        {
                            if (!string.IsNullOrEmpty(pol.AllowedRemoteTcpConnectPorts))
                            {
                                var def = new RuleDef(ex.Id, "TCP Outbound Ports", ex.Subject, RuleAction.Allow, RuleDirection.Out, Protocol.TCP, permitWeight);
                                if (!string.Equals(pol.AllowedRemoteTcpConnectPorts, "*"))
                                    def.RemotePorts = pol.AllowedRemoteTcpConnectPorts;
                                if (pol.LocalNetworkOnly)
                                    def.RemoteAddresses = RuleDef.LOCALSUBNET_ID;
                                results.Add(def);
                            }
                            if (!string.IsNullOrEmpty(pol.AllowedRemoteUdpConnectPorts))
                            {
                                var def = new RuleDef(ex.Id, "UDP Outbound Ports", ex.Subject, RuleAction.Allow, RuleDirection.Out, Protocol.UDP, permitWeight);
                                if (!string.Equals(pol.AllowedRemoteUdpConnectPorts, "*"))
                                    def.RemotePorts = pol.AllowedRemoteUdpConnectPorts;
                                if (pol.LocalNetworkOnly)
                                    def.RemoteAddresses = RuleDef.LOCALSUBNET_ID;
                                results.Add(def);
                            }
                        }
                        break;
                    }
                case PolicyType.RuleList:
                    {
                        // The RuleDefs returned can get modified by the caller.
                        // To avoid changing the original templates we return copies of rules.

                        var pol = (RuleListPolicy)ex.Policy;
                        foreach (var rule in pol.Rules)
                        {
                            var ruleCopy = rule.ShallowCopy();
                            ruleCopy.SetSubject(ex.Subject);
                            ruleCopy.ExceptionId = ex.Id;
                            ruleCopy.Weight = (rule.Action == RuleAction.Allow) ? permitWeight : blockWeight;
                            results.Add(ruleCopy);
                        }
                        break;
                    }
            }
        }

        private static string ConfigSavePath
        {
            get
            {
                return Path.Combine(Utils.AppDataPath, "config");
            }
        }

        private static string ConfigRecoveryPath => ConfigSavePath + ".recovery";

        // pwd holds the password hash. Replace the Users read it inherits from the data
        // directory (also on files written by earlier versions), then hold it open without
        // sharing. Callers reading or writing it through PasswordLock unlock it first.
        private void LockPasswordFile()
        {
            try
            {
                Installer.SecretFileProtection.Ensure(PasswordLock.PasswordFilePath);
            }
            catch (Exception e)
            {
                Utils.LogException(e, Utils.LOG_ID_SERVICE);
            }
            FileLocker.Lock(PasswordLock.PasswordFilePath, FileAccess.Read, FileShare.None);
        }

        private void RestoreInterruptedPolicy()
        {
            bool recoveryExists;
            try
            {
                File.GetAttributes(ConfigRecoveryPath);
                recoveryExists = true;
            }
            catch (FileNotFoundException) { recoveryExists = false; }
            catch (DirectoryNotFoundException) { recoveryExists = false; }
            if (!recoveryExists)
                Diagnostics.Emit(RuntimeEvent.policy_recovery, RuntimeResult.absent);
            PolicyRecoveryJournal.Recover(recoveryExists,
                () => Diagnostics.Run(RuntimeEvent.policy_recovery,
                    () => ServerConfiguration.Load(ConfigRecoveryPath).Save(ConfigSavePath)),
                () => Diagnostics.Run(RuntimeEvent.policy_recovery_clear, () => File.Delete(ConfigRecoveryPath)));
        }

        private ServerConfiguration LoadServerConfig()
        {
            return EnforcementPolicy.LoadConfiguration(
                () => File.GetAttributes(ConfigSavePath),
                () => ServerConfiguration.Load(ConfigSavePath), () =>
                {
                    Diagnostics.Emit(RuntimeEvent.configuration_load, RuntimeResult.absent);
                    return CreateDefaultServerConfig();
                });
        }

        private static ServerConfiguration CreateDefaultServerConfig()
        {
            var ret = new ServerConfiguration { ActiveProfileName = Resources.Messages.Default };

            // Allow recommended exceptions
            DatabaseClasses.AppDatabase db = GlobalInstances.AppDatabase;
            foreach (DatabaseClasses.Application app in db.KnownApplications)
            {
                if (app.HasFlag("TWUI:Special") && app.HasFlag("TWUI:Recommended"))
                {
                    ret.ActiveProfile.SpecialExceptions.Add(app.Name);
                }
            }

            return ret;
        }

        // This method completely reinitializes the firewall.
        private void InitFirewall(FirewallMode? restoreMode = null)
        {
            try { InitializeFirewallPolicy(restoreMode); }
            catch
            {
                // Initialization/reinitialization expires until-reboot grants too; a
                // persistence failure cannot roll those permissions back into service.
                FailClosed();
                throw;
            }
        }

        private void InitializeFirewallPolicy(FirewallMode? restoreMode)
        {
            using var timer = new HierarchicalStopwatch("InitFirewall()");
            EnsureRestrictiveBaseline();
            RestoreInterruptedPolicy();
            LoadDatabase();
            ServerConfiguration candidate = null!;
            Diagnostics.Run(RuntimeEvent.configuration_load, () => candidate = LoadServerConfig());
            if (!DiagnosticSettingLoaded)
            {
                // The recovered on-disk setting is trusted. Later candidates take effect only
                // after a complete successful policy commit, including recovery cleanup.
                Diagnostics.SetEnabled(candidate.EnableDiagnosticLogging);
                DiagnosticSettingLoaded = true;
            }
            // A stored startup mode of Learning (removed) or any other unsupported value starts Normal.
            candidate.StartupMode = FirewallModePolicy.NormalizeStartupMode(candidate.StartupMode);
            PruneExpiredRules(candidate, restarting: true);
            if (ActiveConfig.Service == null)
            {
                // Initial state is not served until Run has committed protection and reported readiness.
                ActiveConfig.Service = candidate;
                VisibleState.Mode = candidate.StartupMode;
            }
            // A rebuild keeps the revoked runtime mode.
            FirewallMode mode = restoreMode is FirewallMode restored && FirewallModePolicy.IsRuntimeMode(restored)
                ? restored : candidate.StartupMode;
            ApplyConfiguration(candidate, mode);
        }


        // This method reapplies all firewall settings.
        private void ReapplySettings()
        {
            using var timer = new HierarchicalStopwatch("ReapplySettings()");
            // Hosts locking is best-effort and must never gate WFP enforcement.
            bool lockHosts = PolicyConfiguration.LockHostsFile;
            bool protectionApplied = HostsFileManager.TryApplyProtection(lockHosts, out Exception? protectionError);
            if (protectionError != null)
            {
                Utils.Log("Could not apply hosts file protection. Firewall enforcement continues without it.", Utils.LOG_ID_SERVICE);
                Utils.LogException(protectionError, Utils.LOG_ID_SERVICE);
            }
            if (PolicyConfiguration.Blocklists.EnableBlocklists
                && PolicyConfiguration.Blocklists.EnableHostsBlocklist)
                HostsFileManager.EnableHostsFile();
            else
                HostsFileManager.DisableHostsFile();
            // Hosts content changes relock best-effort; a lost lock clears EnableProtection.
            VisibleState.HealthWarnings = ServiceHealthPolicy.Set(VisibleState.HealthWarnings, ServiceHealthWarning.HostsProtection,
                lockHosts && (!protectionApplied || !HostsFileManager.EnableProtection));
            EffectiveHostsBlocklist = PolicyConfiguration.Blocklists.EnableBlocklists && PolicyConfiguration.Blocklists.EnableHostsBlocklist;
            Diagnostics.Emit(RuntimeEvent.hosts_blocklist_state, EffectiveHostsBlocklist ? RuntimeResult.enabled : RuntimeResult.disabled);
        }

        private void ReportEffectiveDiagnosticState()
        {
            Diagnostics.Emit(RuntimeEvent.port_blocklist_state, EffectivePortBlocklist ? RuntimeResult.enabled : RuntimeResult.disabled);
            Diagnostics.Emit(RuntimeEvent.port_blocklist_rules, PortBlocklistDiagnosticsAvailable
                ? (PortBlocklistFilterIds.Any ? RuntimeResult.present : RuntimeResult.absent) : RuntimeResult.failure);
            Diagnostics.Emit(RuntimeEvent.hosts_blocklist_state, EffectiveHostsBlocklist ? RuntimeResult.enabled : RuntimeResult.disabled);
            Diagnostics.Emit(RuntimeEvent.hosts_protection, HostsFileManager.EnableProtection ? RuntimeResult.enabled : RuntimeResult.disabled);
            bool available = LogWatcher.AuditEnrichmentAvailable;
            Diagnostics.SetAuditAvailable(available);
            if (Diagnostics.Enabled)
                LastDiagnosticAuditState = available ? 1 : 0;
            Diagnostics.Emit(RuntimeEvent.audit_health, available ? RuntimeResult.enabled : RuntimeResult.disabled,
                available ? 0 : LogWatcher.SubscriptionHResult);
        }

        private void LoadDatabase()
        {
            using var timer = new HierarchicalStopwatch("LoadDatabase()");

            try
            {
                Diagnostics.Run(RuntimeEvent.database_load, () => GlobalInstances.AppDatabase = DatabaseClasses.AppDatabase.Load());
            }
            catch
            {
                GlobalInstances.AppDatabase = new DatabaseClasses.AppDatabase();
                Diagnostics.Emit(RuntimeEvent.database_load, RuntimeResult.fallback);
            }
        }

        internal void TimerCallback(Object state)
        {
            Diagnostics.SetAuditAvailable(LogWatcher.AuditEnrichmentAvailable);
            Q.Add(new TwRequest(TwMessageSimple.CreateRequest(MessageType.MINUTE_TIMER)));
        }

        private List<FirewallLogEntry> GetFwLog()
        {
            var entries = new List<FirewallLogEntry>();
            lock (FirewallLogEntries)
            {
                var identities = new Dictionary<uint, (bool Open, string Path, long Creation)>();
                ResolveActivityPids(DateTime.Now - ActivityAuditWindow - TimeSpan.FromSeconds(1), identities);
                // A PID the matched process no longer holds is dropped for good: the tray app,
                // which may not open service hosts, relies on this check being current.
                foreach (FirewallLogEntry entry in FirewallLogEntries)
                {
                    if (entry.ProcessId != 0 &&
                        (!IdentityOf(entry.ProcessId, identities, out string _, out long creation) || creation != entry.ProcessCreation))
                        entry.ProcessId = 0;
                }
                // Copies: the service keeps changing its rows after the lock is released.
                foreach (FirewallLogEntry entry in FirewallLogEntries)
                    entries.Add(entry with { });
            }
            return entries;
        }

        // alwaysAllow also saves an Always allow rule for the file name and the signer the
        // service read from the prompt's file itself; the controller supplies only the token.
        private bool ApplyPromptAllow(BlockedConnectionPrompt prompt, bool alwaysAllow, SignedFileIdentity? signer)
        {
            if (VisibleState.Mode != FirewallMode.Normal)
                return false;

            // Recheck the file right before the exception is written: the path was captured
            // when the connection was blocked, and the binary may be gone by the time the
            // user clicks Allow.
            if (!PromptAllowPolicy.TryCreate(prompt.Identity, File.Exists, out PromptAllowPolicy? allowPolicy, out string? refusalReason) ||
                allowPolicy == null)
            {
                Utils.Log("Refused a prompt allow: " + (refusalReason ?? "no policy could be created."), Utils.LOG_ID_SERVICE);
                return false;
            }

            ExceptionSubject subject;
            switch (allowPolicy.Identity.Kind)
            {
                case PromptIdentityKind.Executable:
                    subject = new ExecutableSubject(allowPolicy.Identity.ExecutablePath!);
                    break;
                case PromptIdentityKind.Service:
                    subject = new ServiceSubject(
                        allowPolicy.Identity.ExecutablePath!,
                        allowPolicy.Identity.ServiceName!);
                    break;
                case PromptIdentityKind.Package:
                    string packageSid = allowPolicy.Identity.PackageSid!;
                    subject = new AppContainerSubject(packageSid, packageSid, string.Empty, string.Empty);
                    break;
                default:
                    return false;
            }

            var policy = new TcpUdpPolicy
            {
                AllowedRemoteTcpConnectPorts = allowPolicy.AllowedRemoteTcpConnectPorts,
                AllowedRemoteUdpConnectPorts = allowPolicy.AllowedRemoteUdpConnectPorts,
                AllowedLocalTcpListenerPorts = allowPolicy.AllowedLocalTcpListenerPorts,
                AllowedLocalUdpListenerPorts = allowPolicy.AllowedLocalUdpListenerPorts,
            };
            var exception = new FirewallExceptionV3(subject, policy);
            if (HasExplicitBlockForSubject(subject))
                return false;
            var candidate = Utils.DeepClone(ActiveConfig.Service);
            candidate.ActiveProfile.AddExceptions(new List<FirewallExceptionV3> { exception });
            if (alwaysAllow)
            {
                string? path = allowPolicy.Identity.ExecutablePath;
                if (signer == null || allowPolicy.Identity.Kind != PromptIdentityKind.Executable)
                {
                    Utils.Log("Refused Always allow: " + (path ?? "the subject") +
                        " is not an executable with a valid embedded signature.", Utils.LOG_ID_SERVICE);
                    return false;
                }
                if (allowPolicy.Identity.CoveredServiceNames.Count != 0)
                {
                    Utils.Log("Refused Always allow: " + path + " is the executable of Windows services.", Utils.LOG_ID_SERVICE);
                    return false;
                }
                AlwaysAllowRule.AddTo(candidate.ActiveProfile.AlwaysAllowRules,
                    new AlwaysAllowRule(Path.GetFileName(path!), signer.Subject, signer.Name));
            }
            try
            {
                ApplyConfiguration(candidate, VisibleState.Mode);
                if (alwaysAllow)
                    Utils.Log("Always allow saved a rule for " + Path.GetFileName(allowPolicy.Identity.ExecutablePath) +
                        " signed by " + signer!.Subject + ".", Utils.LOG_ID_SERVICE);
                return true;
            }
            catch (Exception exceptionToLog)
            {
                Utils.LogException(exceptionToLog, Utils.LOG_ID_SERVICE);
                return false;
            }
        }

        // The entry the popup's Ignore button would add for this identity, or null when Ignore
        // saves nothing: a shared service host, a mode other than Normal, or an app already listed.
        private IgnoredApp? IgnoredEntryToAdd(PromptIdentity? identity)
        {
            IgnoredApp? app = identity == null ? null : IgnoredApp.FromIdentity(identity);
            if (app == null || !app.IsValid || VisibleState.Mode != FirewallMode.Normal || RuntimeSessionRevoked ||
                ActiveConfig.Service.ActiveProfile.IgnoredApps.Exists(existing => existing != null && existing.SameEntry(app)))
                return null;
            return app;
        }

        // The popup's Ignore button. The Ignored list is a saved setting, so it changes only while
        // the service is unlocked; IGNORE_PROMPT answers Locked first so the popup can ask for the password.
        private void RememberIgnored(PromptIdentity identity)
        {
            IgnoredApp? app = IgnoredEntryToAdd(identity);
            if (app == null || PasswordLock.Locked)
                return;
            var candidate = Utils.DeepClone(ActiveConfig.Service);
            if (!IgnoredApp.AddTo(candidate.ActiveProfile.IgnoredApps, app))
                return;
            try
            {
                ApplyConfiguration(candidate, VisibleState.Mode);
                Utils.Log("Ignore added " + app.DisplayName + " (" + app.Detail + ") to the Ignored list. It stays blocked and is no longer prompted.", Utils.LOG_ID_SERVICE);
            }
            catch (Exception exception)
            {
                Utils.LogException(exception, Utils.LOG_ID_SERVICE);
            }
        }

        // True when a change adds or removes Ignored entries and nothing else. The list changes no
        // filter, so pending prompts for other apps stay valid.
        private static bool IsIgnoredAppsOnlyChange(ServerConfiguration previous, ServerConfiguration candidate, bool modeUnchanged)
        {
            List<IgnoredApp> before = previous.ActiveProfile.IgnoredApps, now = candidate.ActiveProfile.IgnoredApps;
            // Cheap exit first: Allow applies its change while holding the prompt-queue lock.
            if (!modeUnchanged || (before.Count == now.Count && before.TrueForAll(app => now.Exists(other => other.SameEntry(app)))))
                return false;
            byte[] after = SerializationHelper.Serialize(candidate);
            ServerConfiguration copy = Utils.DeepClone(previous);
            copy.ActiveProfile.IgnoredApps = candidate.ActiveProfile.IgnoredApps;
            return SerializationHelper.Serialize(copy).SequenceEqual(after);
        }

        // An app removed from the Ignored list prompts again on its next block, even within the
        // five-minute cooldown its Ignore started.
        private void EndRemovedIgnoreCooldowns(ServerConfiguration previous, ServerConfiguration candidate)
        {
            List<IgnoredApp> kept = candidate.ActiveProfile.IgnoredApps;
            List<IgnoredApp> removed = previous.ActiveProfile.IgnoredApps.FindAll(
                app => app.IsValid && !kept.Exists(other => other != null && other.SameEntry(app)));
            if (removed.Count > 0)
                BlockedPromptQueue.EndCooldowns(identity => removed.Exists(app => app.Matches(identity)));
        }

        private void ApplyConfiguration(ServerConfiguration candidate, FirewallMode mode)
        {
            if (!FirewallModePolicy.IsRuntimeMode(mode) || !FirewallModePolicy.IsStartupMode(candidate.StartupMode))
                throw new ArgumentException("Unsupported runtime or startup firewall mode.");
            // A revoked session has no runtime policy to replace; the rebuild reloads stored policy.
            if (RuntimeSessionRevoked)
                throw new InvalidOperationException("Runtime permissions were withdrawn; policy is being restored.");
            ServerConfiguration previous = ActiveConfig.Service;
            ApplyingConfiguration = candidate;
            ApplyingMode = mode;
            try
            {
                PreservePromptTokens = AiExplainEgressPolicy.IsEgressOnlyChange(previous, candidate,
                    mode == VisibleState.Mode, config => config.AiAssistantEgress, (config, flag) =>
                    {
                        ServerConfiguration copy = Utils.DeepClone(config);
                        copy.AiAssistantEgress = flag;
                        return SerializationHelper.Serialize(copy);
                    }) || IsIgnoredAppsOnlyChange(previous, candidate, mode == VisibleState.Mode);
            }
            catch (Exception exception)
            {
                // Comparison failure only means the normal token revocation applies.
                PreservePromptTokens = false;
                Utils.LogException(exception, Utils.LOG_ID_SERVICE);
            }
            try
            {
                Diagnostics.CommitConfiguration(candidate.EnableDiagnosticLogging, () => PolicyChangeTransaction.Apply(
                    () => Diagnostics.Run(RuntimeEvent.policy_persist, () => candidate.Save(ConfigSavePath)),
                    () =>
                    {
                        ReapplySettings();
                        InstallFirewallRules();
                    },
                    () => Diagnostics.Run(RuntimeEvent.policy_rollback, () =>
                    {
                        previous.Save(ConfigSavePath);
                        ApplyingConfiguration = previous;
                        ApplyingMode = VisibleState.Mode;
                        ReapplySettings();
                    }),
                    () => Diagnostics.Run(RuntimeEvent.policy_publish, () =>
                    {
                        ActiveConfig.Service = candidate;
                        lock (BlockedPromptQueue.SyncRoot)
                        {
                            ResetPromptCandidates(revokeTokens: !PreservePromptTokens);
                            VisibleState.Mode = mode;
                            EndRemovedIgnoreCooldowns(previous, candidate);
                        }
                        GlobalInstances.ServerChangeset = Guid.NewGuid();
                    }),
                    FailClosed,
                    () => Diagnostics.Run(RuntimeEvent.policy_journal, () => previous.Save(ConfigRecoveryPath)),
                    () => Diagnostics.Run(RuntimeEvent.policy_recovery_clear, () => File.Delete(ConfigRecoveryPath))));
                ReportEffectiveDiagnosticState();
            }
            finally
            {
                ApplyingConfiguration = null;
                ApplyingMode = null;
                PreservePromptTokens = false;
            }
        }

        private void DisposeRuntimeSubscription()
        {
            // Keep the callback delegate rooted until the engine is closed, including
            // when SafeHandle.Dispose silently consumes a failed native unsubscribe.
            Diagnostics.Run(RuntimeEvent.wfp_unsubscribe, () => RuntimeEventSubscription?.Dispose());
        }

        private void FailClosed()
        {
            Diagnostics.Run(RuntimeEvent.fail_closed, FailClosedCore);
        }

        private void FailClosedCore()
        {
            RuntimeStopping = true;
            ResetPromptCandidates(stop: true);
            // Revocation no longer ends the worker: the loop rebuilds the session in-process
            // while the persistent deny baseline holds (RebuildRuntimeSession).
            if (VisibleState.Mode != FirewallMode.Unknown)
                RevokedMode = VisibleState.Mode;
            if (!RuntimeSessionRevoked)
            {
                // Consume the subscription SafeHandle before invalidating the engine. Even
                // a failed native unsubscribe cannot delay the checked native session close.
                RuntimeSessionRevocation.Close(WfpEngine.NativePtr,
                    FwpmEngineSafeHandle.NativeMethods.FwpmEngineClose0,
                    error =>
                    {
                        Diagnostics.Emit(RuntimeEvent.fail_closed, RuntimeResult.failure, error.HResult);
                        Environment.FailFast("SecureWall cannot confirm withdrawal of runtime permissions.", error);
                    },
                    DisposeRuntimeSubscription);
                RuntimeSessionRevoked = true;
                RuntimeEventSubscription = null;
            }
            PromptableFilterIds.Replace(Array.Empty<ulong>());
            PortBlocklistFilterIds.Clear();
            FilterGroups.Clear();
            PortBlocklistDiagnosticsAvailable = true;
            EffectivePortBlocklist = false;
            // The closed session took the permits with it.
            foreach (UpdateNetworkLease lease in UpdateLeases.Clear())
                lease.Process?.Dispose();
            VisibleState.Mode = FirewallMode.Unknown;
            GlobalInstances.ServerChangeset = Guid.NewGuid();
            PasswordLock.Locked = true;
        }

        private static Engine CreateRuntimeEngine() =>
            new("SecureWall Session", "", FWPM_SESSION_FLAGS.FWPM_SESSION_FLAG_DYNAMIC, 5000);

        // Net event options are global BFE settings shared with other WFP consumers and
        // persist across reboots. FwpmEngineSetOption0 fails with
        // FWP_E_DYNAMIC_SESSION_IN_PROGRESS inside a dynamic session such as the runtime
        // one, so they go through a short-lived ordinary session that adds no objects.
        private readonly struct NetEventOptions
        {
            internal readonly bool Collect;
            internal readonly InboundEventMatchKeyword Keywords;
            internal NetEventOptions(bool collect, InboundEventMatchKeyword keywords) { Collect = collect; Keywords = keywords; }
        }

        private static readonly NetEventOptions RuntimeNetEventOptions = new(true,
            InboundEventMatchKeyword.FWPM_NET_EVENT_KEYWORD_INBOUND_BCAST | InboundEventMatchKeyword.FWPM_NET_EVENT_KEYWORD_INBOUND_MCAST);

        private static Engine OpenEngineOptionsSession() => new("SecureWall Engine Options", "", FWPM_SESSION_FLAGS.None, 5000);

        private static NetEventOptions ReadNetEventOptions()
        {
            using var options = OpenEngineOptionsSession();
            return new NetEventOptions(options.CollectNetEvents, options.EventMatchAnyKeywords);
        }

        private static void ApplyNetEventOptions(NetEventOptions value)
        {
            using var options = OpenEngineOptionsSession();
            options.CollectNetEvents = value.Collect;
            options.EventMatchAnyKeywords = value.Keywords;
        }

        private bool MayRetainCommittedPolicy() => EnvironmentalPolicyReload.MayRetain(!RuntimeSessionRevoked, LastInstallCommitted,
            Addresses.Superseded || VolumeMappingChanged ||
                DisplayRestriction.Pending(ActiveConfig.Service.ActiveProfile.DisplayOffBlock, DisplayCurrentlyOn, CommittedDisplayRestricted),
            ReloadRetry.Failures);

        private void RetainCommittedPolicy(Exception error)
        {
            // The previous policy stays; update permits do not.
            RemoveUpdateLeases(UpdateLeases.Clear());
            ReloadRetry.RecordFailure(RecoveryClock.Elapsed);
            Utils.Log("Policy reload failed before any WFP change committed; the previous policy stays in force. Retry " +
                ReloadRetry.Failures + " of " + EnvironmentalPolicyReload.MaxRetainedFailures +
                " is scheduled. A failure after the last retry withdraws runtime permissions.", Utils.LOG_ID_SERVICE);
            Utils.LogException(error, Utils.LOG_ID_SERVICE);
        }

        // Enforcement thread only. refresh updates environment facts and reports whether
        // policy must be reinstalled. A failure is either retained with a bounded retry or
        // withdraws runtime grants (then the worker loop rebuilds the session).
        private void ReloadForEnvironment(RuntimeEvent? diagnostic, Func<bool> refresh)
        {
            // A pending rebuild re-reads the environment before it installs anything.
            if (RuntimeSessionRevoked)
                return;
            // A failure before InstallFirewallRulesCore starts must not see an earlier commit.
            LastInstallCommitted = false;
            void Reload()
            {
                if (refresh())
                    InstallFirewallRules();
            }
            EnvironmentalPolicyReload.Run(diagnostic is RuntimeEvent code ? () => Diagnostics.Run(code, Reload) : Reload,
                MayRetainCommittedPolicy, RetainCommittedPolicy, FailClosed);
        }

        // Enforcement thread only, after FailClosed. The persistent deny baseline holds
        // throughout; nothing is granted until the stored policy commits in a new session.
        private void RebuildRuntimeSession()
        {
            Diagnostics.Run(RuntimeEvent.wfp_subscribe, () =>
            {
                Engine engine = CreateRuntimeEngine();
                IDisposable? subscription = null;
                try
                {
                    ApplyNetEventOptions(RuntimeNetEventOptions);
                    subscription = engine.SubscribeNetEvent(WfpNetEventCallback);
                }
                catch
                {
                    try { subscription?.Dispose(); } catch { }
                    engine.Dispose();
                    throw;
                }
                // The revoked handle was already closed natively and marked invalid.
                Engine revoked = WfpEngine;
                WfpEngine = engine;
                RuntimeEventSubscription = subscription;
                RuntimeFilterKeys.Clear();
                RuntimeSessionRevoked = false;
                try { revoked.Dispose(); } catch { }
            });
            // Address-scoped grants need a current enumeration; a failure revokes the new session.
            try { Diagnostics.Run(RuntimeEvent.network_reload, () => ReenumerateAdresses()); }
            catch
            {
                FailClosed();
                throw;
            }
            InitFirewall(RevokedMode);
            RuntimeStopping = false;
            CorrelatedDrops.Resume();
        }

        private void RunDueRecovery()
        {
            switch (RuntimeRecovery.Due(RuntimeSessionRevoked, SessionRebuild, ReloadRetry, RecoveryClock.Elapsed))
            {
                case RuntimeRecoveryStep.RebuildSession:
                    try
                    {
                        RebuildRuntimeSession();
                        SessionRebuild.Reset();
                        Utils.Log("Runtime WFP session rebuilt; stored policy is enforced again.", Utils.LOG_ID_SERVICE);
                    }
                    catch (Exception error)
                    {
                        if (!RuntimeSessionRevoked)
                            FailClosed();
                        SessionRebuild.RecordFailure(RecoveryClock.Elapsed);
                        Utils.Log("Rebuilding the runtime WFP session failed (attempt " + SessionRebuild.Failures + " of " +
                            RuntimeRecovery.MaxRebuildAttempts + "). The persistent deny baseline stays in force.", Utils.LOG_ID_SERVICE);
                        Utils.LogException(error, Utils.LOG_ID_SERVICE);
                        if (RuntimeRecovery.Exhausted(SessionRebuild))
                            throw new InvalidOperationException("SecureWall could not restore runtime policy; exiting for SCM recovery.", error);
                    }
                    break;
                case RuntimeRecoveryStep.RetryReload:
                    // Nothing observed a change if enumeration had failed, so re-read it first.
                    try { ReloadForEnvironment(RuntimeEvent.network_reload, () => { ReenumerateAdresses(); return true; }); }
                    catch (Exception error) { Utils.LogException(error, Utils.LOG_ID_SERVICE); }
                    break;
            }
        }

        private void ExpireRules()
        {
            Diagnostics.Run(RuntimeEvent.rule_expiry, () => ExpiringPolicyMaintenance.Run(
                () => Utils.DeepClone(ActiveConfig.Service),
                candidate => PruneExpiredRules(candidate),
                candidate => ApplyConfiguration(candidate, VisibleState.Mode),
                FailClosed));
        }

        private static bool HasExplicitBlockForSubject(ExceptionSubject subject)
        {
            foreach (FirewallExceptionV3 existing in ActiveConfig.Service.ActiveProfile.AppExceptions)
            {
                if (!existing.Subject.Equals(subject))
                    continue;
                if (existing.Policy is HardBlockPolicy)
                    return true;
                if (existing.Policy is RuleListPolicy rules &&
                    rules.Rules.Any(rule => rule.Action == RuleAction.Block))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasSystemRebooted()
        {
            try
            {
                const string ATOM_NAME = "SecureWall-NoMachineReboot";
                bool rebooted = !GlobalAtomTable.Exists(ATOM_NAME);
                if (rebooted)
                    GlobalAtomTable.Add(ATOM_NAME);
                return rebooted;
            }
            catch
            {
                return true;
            }
        }

        private static bool PruneExpiredRules(ServerConfiguration configuration, bool restarting = false)
        {
            // Service restart is also a conservative boundary for until-reboot grants.
            bool system_rebooted = HasSystemRebooted() || restarting;
            bool config_changed = false;

            List<FirewallExceptionV3> exs = configuration.ActiveProfile.AppExceptions;
            for (int i = exs.Count - 1; i >= 0; --i)
            {
                // Timer values above zero are the number of minutes to stay active

                if (system_rebooted && (exs[i].Timer == AppExceptionTimer.Until_Reboot))
                {
                    exs.RemoveAt(i);
                    config_changed = true;
                }
                else if (EnforcementPolicy.IsExpired(exs[i].CreationDate, (int)exs[i].Timer, DateTime.Now))
                {
                    exs.RemoveAt(i);
                    config_changed = true;
                }
            }

            if (config_changed)
            {
                configuration.ActiveProfile.AppExceptions = exs;
            }

            return config_changed;
        }

        private TwMessage ProcessCmd(TwMessage req, uint clientProcessId)
        {
            switch (req.Type)
            {
                case MessageType.UPDATE_NETWORK_GRANT:
                    {
                        // The pipe thread checked the lock when the request arrived; an inactivity
                        // lock may have fired since.
                        if (PasswordLock.Locked)
                            return TwMessageLocked.Instance;
                        var args = (TwMessageUpdateNetwork)req;
                        UpdateGrantStatus status = GrantUpdateNetwork(clientProcessId, out int secondsLeft);
                        return args.CreateResponse(status, secondsLeft);
                    }
                case MessageType.UPDATE_NETWORK_RELEASE:
                    {
                        var args = (TwMessageUpdateNetwork)req;
                        ReleaseUpdateNetwork(clientProcessId);
                        return args.CreateResponse(UpdateGrantStatus.NotNeeded);
                    }
                case MessageType.UPDATE_NETWORK_REVIEW:
                    {
                        var args = (TwMessageSimple)req;
                        ReviewUpdateLeases();
                        return args.CreateResponse();
                    }
                case MessageType.READ_FW_LOG:
                    {
                        var args = (TwMessageReadFwLog)req;
                        return args.CreateResponse(GetFwLog().ToArray());
                    }
                case MessageType.READ_PENDING_PROMPTS:
                    {
                        var args = (TwMessageReadPendingPrompts)req;
                        PromptWireDto[] prompts = BlockedPromptQueue
                            .GetPending()
                            .Select(PromptWireDto.FromPrompt)
                            .ToArray();
                        RefreshAttributionState();
                        return args.CreateResponse(prompts, Utils.DeepClone(VisibleState));
                    }
                case MessageType.DISMISS_PROMPT:
                case MessageType.DISMISS_PROMPT_TIMEOUT:
                    {
                        var args = (TwMessagePromptAction)req;
                        PromptActionResult result = BlockedPromptQueue.Dismiss(args.Token);
                        Diagnostics.Emit(RuntimeEvent.prompt_ignore, RuntimeJournal.PromptResult(result.Status));
                        return args.CreateResponse(result.Status);
                    }
                case MessageType.IGNORE_PROMPT:
                    {
                        var args = (TwMessagePromptAction)req;
                        // Locked and there is an entry to save: keep the prompt pending and answer
                        // Locked, so the popup asks for the password and retries, as Allow does.
                        if (PasswordLock.Locked && IgnoredEntryToAdd(BlockedPromptQueue.PendingIdentity(args.Token)) != null)
                        {
                            Diagnostics.Emit(RuntimeEvent.prompt_ignore, RuntimeResult.locked);
                            return args.CreateResponse(PromptActionStatus.Locked);
                        }
                        PromptActionResult result = BlockedPromptQueue.Dismiss(args.Token);
                        Diagnostics.Emit(RuntimeEvent.prompt_ignore, RuntimeJournal.PromptResult(result.Status));
                        // Saved after the queue lock is released. A failed save still leaves the cooldown.
                        if (result.Status == PromptActionStatus.Dismissed && result.Prompt != null)
                            RememberIgnored(result.Prompt.Identity);
                        return args.CreateResponse(result.Status);
                    }
                case MessageType.ALLOW_PROMPT:
                case MessageType.ALLOW_PROMPT_ALWAYS:
                    {
                        var args = (TwMessagePromptAction)req;
                        bool alwaysAllow = args.Type == MessageType.ALLOW_PROMPT_ALWAYS;
                        // The signature check hashes the whole file: run it before Allow takes the
                        // queue lock, so drops arriving meanwhile still become prompts.
                        SignedFileIdentity? signer = alwaysAllow
                            ? SignedFileIdentity.TryRead(BlockedPromptQueue.PendingIdentity(args.Token)?.ExecutablePath)
                            : null;
                        PromptActionResult result = BlockedPromptQueue.Allow(args.Token, prompt => ApplyPromptAllow(prompt, alwaysAllow, signer));
                        Diagnostics.Emit(RuntimeEvent.prompt_allow, RuntimeJournal.PromptResult(result.Status));
                        return args.CreateResponse(result.Status);
                    }
                case MessageType.AUTO_ALLOW_SIGNED:
                    {
                        var args = (TwMessageSimple)req;
                        ApplyQueuedAlwaysAllows();
                        return args.CreateResponse();
                    }
                case MessageType.IS_LOCKED:
                    {
                        var args = (TwMessageIsLocked)req;
                        return args.CreateResponse(PasswordLock.Locked);
                    }
                case MessageType.MODE_SWITCH:
                    {
                        var args = (TwMessageModeSwitch)req;
                        FirewallMode newMode = args.Mode;
                        // Learning was removed. Its reserved value, like any unknown mode, is
                        // refused before any policy work, so the current mode stays in force.
                        if (!FirewallModePolicy.IsRuntimeMode(newMode))
                        {
                            Utils.Log("Refused a switch to unsupported firewall mode " + (int)newMode +
                                "; the current mode is unchanged.", Utils.LOG_ID_SERVICE);
                            return TwMessageError.Instance;
                        }

                        var candidate = Utils.DeepClone(ActiveConfig.Service);
                        if (FirewallModePolicy.IsStartupMode(newMode))
                            candidate.StartupMode = newMode;
                        ApplyConfiguration(candidate, newMode);
                        return args.CreateResponse(VisibleState.Mode);
                    }
                case MessageType.PUT_SETTINGS:
                    {
                        var args = (TwMessagePutSettings)req;

                        bool warning = (args.Changeset != GlobalInstances.ServerChangeset);
                        if (!warning)
                        {
                            try
                            {
                                ServerConfiguration candidate = Utils.DeepClone(args.Config);
                                // An imported configuration that names the removed Learning mode starts Normal.
                                candidate.StartupMode = FirewallModePolicy.MigrateStartupMode(candidate.StartupMode);
                                ApplyConfiguration(candidate, VisibleState.Mode);
                            }
                            catch (Exception e)
                            {
                                Utils.LogException(e, Utils.LOG_ID_SERVICE);
                                return TwMessageError.Instance;
                            }
                        }
                        VisibleState.HasPassword = PasswordLock.HasPassword;
                        VisibleState.Locked = PasswordLock.Locked;
                        return args.CreateResponse(GlobalInstances.ServerChangeset, ActiveConfig.Service, VisibleState, warning);
                    }
                case MessageType.ADD_TEMPORARY_EXCEPTION:
                    {
                        return TemporaryExceptionEnforcement.Run(() =>
                        {
                            var rules = new List<RuleDef>();
                            var rawSocketExceptions = new List<RuleDef>();
                            var args = (TwMessageAddTempException)req;
                            if (PolicyMode == FirewallMode.BlockAll || PolicyMode == FirewallMode.Disabled)
                            {
                                return args.CreateResponse();
                            }

                            lock (InheritanceGuard)
                            {
                                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                foreach (var ex in args.Exceptions)
                                {
                                    if (!(ex.Subject is ExecutableSubject subject) || !paths.Add(subject.ExecutablePath) ||
                                        !ChildInheritedSubjectExes.TryGetValue(subject.ExecutablePath, out var parents))
                                        continue;
                                    foreach (string parent in parents)
                                    {
                                        if (!ChildInheritance.TryGetValue(parent, out var currentExceptions))
                                            continue;
                                        foreach (var current in currentExceptions)
                                            if (!EnforcementPolicy.IsExpired(current.CreationDate, (int)current.Timer, DateTime.Now))
                                                GetRulesForException(new FirewallExceptionV3(subject, current.Policy), rules,
                                                    rawSocketExceptions, (ulong)FilterWeights.UserPermit, (ulong)FilterWeights.UserBlock);
                                    }
                                }
                                _ = InstallRules(rules, rawSocketExceptions, true);
                            }

                            return args.CreateResponse();
                        }, ReleaseFirewallThreadPriority);
                    }
                case MessageType.GET_SETTINGS:
                    {
                        var args = (TwMessageGetSettings)req;
                        RefreshAttributionState();

                        // If our changeset is different from the client's, send new settings
                        if (args.Changeset != GlobalInstances.ServerChangeset)
                        {
                            VisibleState.HasPassword = PasswordLock.HasPassword;
                            VisibleState.Locked = PasswordLock.Locked;

                            var ret = args.CreateResponse(GlobalInstances.ServerChangeset, ActiveConfig.Service, Utils.DeepClone(VisibleState));
                            VisibleState.ClientNotifs.Clear();
                            return ret;
                        }
                        else
                        {
                            // Our changeset is the same, so do not send settings again
                            return args.CreateResponse(GlobalInstances.ServerChangeset);
                        }
                    }
                case MessageType.REINIT:
                    {
                        var args = (TwMessageSimple)req;
                        InitFirewall();
                        return args.CreateResponse();
                    }
                case MessageType.RELOAD_WFP_FILTERS:
                    {
                        var args = (TwMessageSimple)req;
                        // Only volume/mount changes send this. Committed filters name volume
                        // devices; a remapped or reused device name cannot be proven safe to keep.
                        VolumeMappingChanged = true;
                        ReloadForEnvironment(null, () => true);
                        return args.CreateResponse();
                    }
                case MessageType.UNLOCK:
                    {
                        var args = (TwMessageUnlock)req;
                        bool success;
                        FileLocker.Unlock(PasswordLock.PasswordFilePath);
                        try
                        {
                            success = PasswordLock.Unlock(args.Password);
                        }
                        finally
                        {
                            LockPasswordFile();
                        }
                        if (success)
                            return args.CreateResponse();
                        else
                            return TwMessageError.Instance;
                    }
                case MessageType.LOCK:
                    {
                        var args = (TwMessageSimple)req;
                        PasswordLock.Locked = true;
                        RemoveUpdateLeases(UpdateLeases.Clear());
                        return args.CreateResponse();
                    }
                case MessageType.GET_PROCESS_PATH:
                    {
                        var args = (TwMessageGetProcessPath)req;
                        string path = Utils.GetPathOfProcess(args.Pid);
                        if (string.IsNullOrEmpty(path))
                            return TwMessageError.Instance;
                        else
                            return args.CreateResponse(path);
                    }
                case MessageType.SET_PASSPHRASE:
                    {
                        var args = (TwMessageSetPassword)req;
                        FileLocker.Unlock(PasswordLock.PasswordFilePath);
                        try
                        {
                            PasswordLock.SetPass(args.Password);
                            GlobalInstances.ServerChangeset = Guid.NewGuid();
                            return args.CreateResponse();
                        }
                        catch
                        {
                            return TwMessageError.Instance;
                        }
                        finally
                        {
                            LockPasswordFile();
                        }
                    }
                case MessageType.STOP_SERVICE:
                    {
                        var args = (TwMessageSimple)req;
                        RunService = false;
                        return args.CreateResponse();
                    }
                case MessageType.MINUTE_TIMER:
                    {
                        var args = (TwMessageSimple)req;
                        bool rule_reload_needed = false;

                        // A revoked session holds no runtime grants; its rebuild prunes expired rules.
                        if (!RuntimeSessionRevoked)
                        {
                            // Expiry precedes housekeeping which may itself fail. A failed save
                            // or replacement must never preserve an expired dynamic permission.
                            ExpireRules();

                            // Event collection might have been disabled by external process or user after we started up,
                            // so re-enable it if that is the case.
                            if (!WfpEngine.CollectNetEvents)
                                ApplyNetEventOptions(RuntimeNetEventOptions);
                        }

                        // Check for inactivity and lock if necessary
                        ControllerActivity.LockIfExpired(() =>
                        {
                            PasswordLock.Locked = true;
                            if (PasswordLock.Locked)
                                RemoveUpdateLeases(UpdateLeases.Clear());
                        });
                        ReviewUpdateLeases();

                        // Periodically reload all rules.
                        // This is needed to clear out temprary rules added due to child-process rule inheritance.
                        // A pending retry already reinstalls on its own schedule.
                        if (ReloadRetry.Failures == 0 && DateTime.Now - LastRuleReloadTime > TimeSpan.FromMinutes(30))
                        {
                            rule_reload_needed = true;
                        }

                        if (rule_reload_needed)
                        {
                            // Same configuration: it only drops temporary inherited child grants,
                            // which derive from parent permits that are still committed.
                            ReloadForEnvironment(null, () => true);
                        }

                        return args.CreateResponse();
                    }
                case MessageType.REENUMERATE_ADDRESSES:
                    {
                        var args = (TwMessageSimple)req;
                        // ReenumerateAdresses returns true if anything changed.
                        ReloadForEnvironment(RuntimeEvent.network_reload, ReenumerateAdresses);
                        return args.CreateResponse();
                    }
                case MessageType.DISPLAY_POWER_EVENT:
                    {
                        var args = (TwMessageDisplayPowerEvent)req;
                        if (args.PowerOn != DisplayCurrentlyOn)
                        {
                            DisplayCurrentlyOn = args.PowerOn;
                            ReloadForEnvironment(RuntimeEvent.display_reload, () => true);
                        }
                        return args.CreateResponse(args.PowerOn);
                    }
                default:
                    {
                        return TwMessageError.Instance;
                    }
            }
        }

        private bool ReenumerateAdresses()
        {
            using var timer = new HierarchicalStopwatch("NIC enumeration");
            var newLocalSubnetAddreses = new HashSet<IpAddrMask>();
            // Use direct P/Invoke to GetAdaptersAddresses instead of
            // NetworkInterface.GetAllNetworkInterfaces() to avoid native memory leak
            // in iphlpapi!GetPerAdapterInfo -> DNSAPI!Dns_AllocZero (~15KB per call).
            // An enumeration failure or a change leaves committed address-scoped grants superseded,
            // so a failed reload revokes instead of retaining them.
            return Addresses.Refresh(() => EnvironmentalPolicyReload.EnumerationChanged(NetworkAdapterEnumerator.EnumerateActiveAdapters(
                out var unicastList, out var newGatewayAddresses, out var newDnsAddresses), () =>
            {

                foreach (var entry in unicastList)
                {
                    if (entry.IsLoopback || entry.IsLinkLocal)
                        continue;

                    newLocalSubnetAddreses.Add(entry.Subnet);
                }

                newLocalSubnetAddreses.Add(new IpAddrMask(IPAddress.Parse("255.255.255.255")));
                newLocalSubnetAddreses.Add(IpAddrMask.LinkLocal);
                newLocalSubnetAddreses.Add(IpAddrMask.IPv6LinkLocal);
                newLocalSubnetAddreses.Add(IpAddrMask.LinkLocalMulticast);
                newLocalSubnetAddreses.Add(IpAddrMask.AdminScopedMulticast);
                newLocalSubnetAddreses.Add(IpAddrMask.IPv6LinkLocalMulticast);

                // Publishes the sets only after every condition list is rebuilt; a failed build
                // resets them so the next enumeration rebuilds every list.
                bool ipConfigurationChanged = RemoteAddressSets.Update(
                    new[] { newLocalSubnetAddreses, newGatewayAddresses, newDnsAddresses },
                    () =>
                    {
                        LocalSubnetFilterConditions.Clear();
                        GatewayFilterConditions.Clear();
                        DnsFilterConditions.Clear();
                    },
                    (list, addr) => (list == 0 ? LocalSubnetFilterConditions : list == 1 ? GatewayFilterConditions : DnsFilterConditions)
                        .Add(new IpFilterCondition(addr.Address, (byte)addr.PrefixLen, RemoteOrLocal.Remote)));

                return ipConfigurationChanged;
            }));
        }

        private const uint FWP_E_PROVIDER_NOT_FOUND = 0x80320005;

        private static Guid[] OwnSublayerKeys() =>
            ((LayerKeyEnum[])Enum.GetValues(typeof(LayerKeyEnum))).Select(GetSublayerKey).ToArray();

        private static WfpSublayerRecord[] ReadSublayers(Engine wfp) =>
            wfp.GetSublayers().Select(s => new WfpSublayerRecord(s.SublayerKey, s.ProviderKey)).ToArray();

        // Every provider's filters on the layers SecureWall's sublayers serve.
        private static List<WfpFilterRecord> ReadFilters(Engine wfp)
        {
            var filters = new List<WfpFilterRecord>();
            foreach (LayerKeyEnum layer in (LayerKeyEnum[])Enum.GetValues(typeof(LayerKeyEnum)))
            {
                using var enumerator = wfp.EnumerateFilterOwnership(GetLayerKey(layer));
                while (enumerator.MoveNext())
                {
                    FilterOwnership f = enumerator.Current;
                    filters.Add(new WfpFilterRecord(f.FilterKey, f.ProviderKey, f.SublayerKey, f.DisplayName));
                }
            }
            return filters;
        }

        // Refuses activation while TinyWall's provider, a TinyWall-era sublayer, or another
        // provider's filter in a SecureWall or TinyWall-era sublayer exists.
        internal static void RequireWfpCoexistence(Engine wfp, string operation)
        {
            WfpCoexistenceReport report = WfpCoexistencePolicy.FindActivationConflicts(SECUREWALL_PROVIDER_KEY,
                wfp.GetProviders().Select(p => p.providerKey), ReadSublayers(wfp), ReadFilters(wfp),
                OwnSublayerKeys(), WfpSublayerKeys.Legacy);
            if (!report.Clear)
                throw new InvalidOperationException(report.Describe(operation));
        }

        internal static void DeleteWfpObjects(Engine wfp, bool removeLayersAndProvider)
        {
            // WARNING! This method is super-slow if not executed inside a WFP transaction!
            using var timer = new HierarchicalStopwatch("DeleteWfpObjects()");
            WfpSublayerRecord[] sublayers = Array.Empty<WfpSublayerRecord>();
            if (removeLayersAndProvider)
            {
                // Another provider's filter in a SecureWall-owned sublayer makes the sublayer
                // undeletable. Name it before changing anything; the caller's transaction aborts.
                sublayers = ReadSublayers(wfp);
                WfpCoexistenceReport blockers = WfpCoexistencePolicy.FindRemovalConflicts(SECUREWALL_PROVIDER_KEY,
                    sublayers, ReadFilters(wfp));
                if (!blockers.Clear)
                    throw new InvalidOperationException(blockers.Describe("Removing SecureWall's WFP sublayers"));
            }

            // Remove SecureWall's filters in every sublayer, current or legacy. Full removal
            // (baseline replacement, uninstall) searches every layer on the system: after a
            // rolled-back in-place upgrade this older binary must also remove filters a newer
            // version registered in layers it was not compiled to know.
            IEnumerable<Guid> layers = removeLayersAndProvider
                ? wfp.GetLayerKeys()
                : ((LayerKeyEnum[])Enum.GetValues(typeof(LayerKeyEnum))).Select(GetLayerKey);
            foreach (Guid layer in layers)
            {
                foreach (var filterKey in wfp.EnumerateFilterKeys(SECUREWALL_PROVIDER_KEY, layer))
                    wfp.UnregisterFilter(filterKey);
            }

            if (!removeLayersAndProvider)
                return;

            // Remove every sublayer SecureWall's provider owns, whatever its key: current ones,
            // TinyWall-keyed ones from builds up to v0.3.0, and any a newer version added.
            // A failure propagates so the enclosing transaction rolls back.
            foreach (Guid sublayerKey in WfpCoexistencePolicy.SublayersToRemove(SECUREWALL_PROVIDER_KEY, sublayers))
                wfp.UnregisterSublayer(sublayerKey);

            try { wfp.UnregisterProvider(SECUREWALL_PROVIDER_KEY); }
            catch (WfpException e) when (e.ErrorCode == FWP_E_PROVIDER_NOT_FOUND) { }
        }

        internal TinyWallServer(ServiceRuntimeDiagnostics diagnostics)
        {
            Diagnostics = diagnostics;
            HostsFileManager.DiagnosticObserver = Diagnostics.Emit;
            HostsFileManager.DiagnosticEnabled = () => Diagnostics.Enabled;
            CorrelatedDrops = new CorrelatedDropBatch(BlockedPromptQueue.SyncRoot, SystemClock.Instance);
            // Put back audit policy left behind by an unclean exit before any new
            // lease can journal over it. Registry only; MpsSvc is not needed.
            try
            {
                AuditPolicyRestoreResult restored = null!;
                Diagnostics.Run(RuntimeEvent.audit_recovery, () => restored = FirewallLogWatcher.RestoreAuditPolicyFromJournal());
                if (restored.Malformed.Count != 0) Diagnostics.Emit(RuntimeEvent.audit_recovery, RuntimeResult.failure);
                foreach (string record in restored.Malformed)
                    Utils.Log("Skipped malformed audit policy recovery record " + record + "; it is kept in place for inspection.", Utils.LOG_ID_SERVICE);
            }
            catch (Exception exception)
            {
                Utils.Log("Cannot restore the audit policy recorded by a previous run; the record is kept for the next start or uninstall.", Utils.LOG_ID_SERVICE);
                Utils.LogException(exception, Utils.LOG_ID_SERVICE);
            }
            LogWatcher = new FirewallLogWatcher(Diagnostics.Emit);
            Timer? minuteTimer = null;
            Timer? promptCandidateTimer = null;
            PipeServerEndpoint? serverPipe = null;
            try
            {
                // Fire up file protections as soon as possible
                FileLocker.Lock(DatabaseClasses.AppDatabase.DBPath, FileAccess.Read, FileShare.Read);
                LockPasswordFile();

                // Lock configuration if we have a password
                if (PasswordLock.HasPassword)
                    PasswordLock.Locked = true;

                LogWatcher.BlockedConnection += LogWatcherBlockedConnection;
                LogWatcher.IsOwnFilter = id => FilterGroups.Lookup(id) != FilterGroup.Unknown;
                LogWatcher.ActivityAudited += LogWatcherActivityAudited;
                minuteTimer = new Timer(new TimerCallback(TimerCallback), null, Timeout.Infinite, Timeout.Infinite);
                promptCandidateTimer = new Timer(
                    new TimerCallback(PromptCandidateTimerCallback),
                    null,
                    TimeSpan.FromMilliseconds(250),
                    TimeSpan.FromMilliseconds(250));

                // Discover network configuration
                ReenumerateAdresses();

                // Fire up pipe
                PipeServerIdentity.AllowAuthenticatedProcessQueries();
                serverPipe = new PipeServerEndpoint(new PipeDataReceived(PipeServerDataReceived), SecureWallProduct.ControllerPipeName);
                MinuteTimer = minuteTimer;
                PromptCandidateTimer = promptCandidateTimer;
                ServerPipe = serverPipe;
            }
            catch
            {
                serverPipe?.Dispose();
                minuteTimer?.Dispose();
                promptCandidateTimer?.Dispose();
                LogWatcher.BlockedConnection -= LogWatcherBlockedConnection;
                LogWatcher.ActivityAudited -= LogWatcherActivityAudited;
                LogWatcher.Dispose();
                FileLocker.UnlockAll();
                throw;
            }
        }

        // Entry point for thread that actually issues commands to Windows Firewall.
        // Only one thread (this one) is allowed to issue them.
        public void Run(ServiceBase service)
        {
            using var timer = new HierarchicalStopwatch("Service Run()");
            using var NetworkInterfaceWatcher = new IpInterfaceWatcher();
            using var DisplayOffSubscription = SafeHandlePowerSettingNotification.Create(service.ServiceHandle, PowerSetting.GUID_CONSOLE_DISPLAY_STATE, DeviceNotifFlags.DEVICE_NOTIFY_SERVICE_HANDLE);
            using var DeviceNotification = SafeHandleDeviceNotification.Create(service.ServiceHandle, DeviceInterfaceClass.GUID_DEVINTERFACE_VOLUME, DeviceNotifFlags.DEVICE_NOTIFY_SERVICE_HANDLE);
            using var MountPointsWatcher = new RegistryWatcher(@"HKEY_LOCAL_MACHINE\SYSTEM\MountedDevices", true);

            // Restore the options found at start, registered before any change so a
            // partial apply is undone too.
            NetEventOptions startupNetEventOptions = ReadNetEventOptions();
            using var NetEventCollection = new CallbackOnDispose(() => { try { ApplyNetEventOptions(startupNetEventOptions); } catch { } });
            ApplyNetEventOptions(RuntimeNetEventOptions);
            Diagnostics.Run(RuntimeEvent.wfp_subscribe, () => RuntimeEventSubscription = WfpEngine.SubscribeNetEvent(WfpNetEventCallback));
            using var WfpEvent = new CallbackOnDispose(DisposeRuntimeSubscription);

            ProcessStartWatcher.EventArrived += ProcessStartWatcher_EventArrived;
            NetworkInterfaceWatcher.InterfaceChanged += (sender, args) =>
            {
                Q.Add(new TwRequest(TwMessageSimple.CreateRequest(MessageType.REENUMERATE_ADDRESSES)));
            };
            RuleReloadEventMerger.Event += (sender, args) =>
            {
                Q.Add(new TwRequest(TwMessageSimple.CreateRequest(MessageType.RELOAD_WFP_FILTERS)));
            };
            MountPointsWatcher.RegistryChanged += (sender, args) =>
            {
                RuleReloadEventMerger.Pulse();
            };
            MountPointsWatcher.Enabled = true;

            // Do not report the service as running until its initial default-deny
            // transaction has committed successfully.
            WindowsFirewall? WinDefFirewall = null;
            using var RuntimeCleanup = new CallbackOnDispose(() =>
            {
                // This guard also covers initialization/compatibility-constructor failure.
                // Confirm native revocation before potentially blocking COM restoration.
                FailClosed();
                Diagnostics.Run(RuntimeEvent.windows_firewall_stop, () => WinDefFirewall?.Dispose());
            });
            InitFirewall();
            Diagnostics.Run(RuntimeEvent.windows_firewall_start, () => WinDefFirewall = new WindowsFirewall());
            service.FinishStateChange();
            Ready = true;
            Diagnostics.SetAuditAvailable(LogWatcher.AuditEnrichmentAvailable);
            Diagnostics.Emit(RuntimeEvent.service_ready, RuntimeResult.success);
#if !DEBUG
            // Basic software health checks. Policy has committed: a guard failure here is
            // logged and shown in the controller, never thrown (that would withdraw policy
            // and stop the service). Start-up and install paths refuse before this point.
            if (!TinyWallDoctor.EnsureHealth(Utils.LOG_ID_SERVICE, strictGuards: false))
                VisibleState.HealthWarnings = ServiceHealthPolicy.Set(VisibleState.HealthWarnings, ServiceHealthWarning.InstallationGuard, true);
#endif

            MinuteTimer.Change(60000, 60000);
            RunService = true;
            while (RunService)
            {
                // Only a requested stop ends this loop. Revocation is repaired here, and an
                // exhausted rebuild throws so the worker reports a failure exit code.
                RunDueRecovery();
                timer.NewSubTask("Message wait");
                if (!Q.TryTake(out TwRequest req,
                    RuntimeRecovery.Wait(RuntimeSessionRevoked, SessionRebuild, ReloadRetry, RecoveryClock.Elapsed)))
                    continue;

                timer.NewSubTask($"Message {req.Request.Type}");
                try
                {
                    req.Response = ProcessCmd(req.Request, req.ClientProcessId);
                }
                catch (Exception e)
                {
                    Utils.LogException(e, Utils.LOG_ID_SERVICE);
                    req.Response = TwMessageError.Instance;
                }
            }
        }

        private void ReleaseFirewallThreadPriority()
        {
            lock (FirewallThreadThrottler.SynchRoot) { FirewallThreadThrottler.Release(); }
        }

        private void ProcessStartWatcher_EventArrived(object sender, EventArrivedEventArgs e)
        {
            try
            {
                if (RuntimeStopping) return;
                using var throttler = new ThreadThrottler(Thread.CurrentThread, ThreadPriority.Highest, true);
                uint pid = (uint)(e.NewEvent["ProcessID"]);
                string path = ProcessManager.GetProcessPath(pid, ref ProcessStartWatcher_Sbuilder);

                // Skip if we have no path
                if (string.IsNullOrEmpty(path))
                    return;

                List<FirewallExceptionV3>? newExceptions = null;

                lock (InheritanceGuard)
                {
                    // Package SIDs are only needed, and only looked up, if a package has a rule
                    string? PackageOf(uint processId) => UserSubjectPackages.Count == 0 ? null : GetPackageSid(processId);

                    // Start walking up the process tree
                    string? parentPath = RuleInheritance.FindInheritedParent(path, PackageOf(pid),
                        RunningAncestors(pid, PackageOf),
                        UserSubjectExes, UserExecutableWideExes, UserSubjectPackages, ChildInheritance.ContainsKey,
                        p => ChildInheritedSubjectExes.TryGetValue(path, out var childVar) && childVar.Contains(p));

                    if (parentPath != null)
                    {
                        newExceptions = new List<FirewallExceptionV3>();

                        foreach (var userEx in ChildInheritance[parentPath])
                            if (!EnforcementPolicy.IsExpired(userEx.CreationDate, (int)userEx.Timer, DateTime.Now))
                                newExceptions.Add(new FirewallExceptionV3(new ExecutableSubject(path), userEx.Policy)
                                {
                                    Timer = userEx.Timer,
                                    CreationDate = userEx.CreationDate
                                });

                        if (!ChildInheritedSubjectExes.ContainsKey(path))
                            ChildInheritedSubjectExes.Add(path, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                        ChildInheritedSubjectExes[path].Add(parentPath);
                    }
                }

                if (newExceptions != null && !RuntimeStopping)
                {
                    TemporaryExceptionEnforcement.Enqueue(
                        () => { lock (FirewallThreadThrottler.SynchRoot) { FirewallThreadThrottler.Request(); } },
                        () => Q.Add(new TwRequest(TwMessageAddTempException.CreateRequest(newExceptions.ToArray()))),
                        ReleaseFirewallThreadPriority);
                }
            }
            finally
            {
                e.NewEvent.Dispose();
            }
        }

        private void WfpNetEventCallback(NetEventData data)
        {
            // Called from the WFP wrapper on an FWPUClnt RPC thread. An exception
            // thrown from here would cross back into native code and kill the service.
            try
            {
                if (RuntimeStopping) return;
                EventLogEvent eventType;
                if (data.EventType == FWPM_NET_EVENT_TYPE.FWPM_NET_EVENT_TYPE_CLASSIFY_DROP)
                    eventType = EventLogEvent.BLOCKED;
                else if (data.EventType == FWPM_NET_EVENT_TYPE.FWPM_NET_EVENT_TYPE_CLASSIFY_ALLOW)
                    eventType = EventLogEvent.ALLOWED;
                else
                    return;

                Diagnostics.ObserveDecision(eventType == EventLogEvent.ALLOWED);
                if (Diagnostics.Enabled && eventType == EventLogEvent.BLOCKED && data.filterId.HasValue && PortBlocklistFilterIds.Contains(data.filterId.Value))
                    Diagnostics.ObservePortBlocklistDrop();

                var entry = new FirewallLogEntry
                {
                    Timestamp = data.timeStamp,
                    Event = eventType,
                    PackageId = data.packageId,
                    RemoteIp = data.remoteAddr?.ToString(),
                    LocalIp = data.localAddr?.ToString()
                };

                if (!Utils.IsNullOrEmpty(data.appId))
                    entry.AppPath = PathMapper.Instance.ConvertPathIgnoreErrors(data.appId, PathFormat.Win32);
                else
                    entry.AppPath = "System";
                if (data.remotePort.HasValue)
                    entry.RemotePort = data.remotePort.Value;
                if (data.direction.HasValue)
                    entry.Direction = data.direction == FwpmDirection.FWP_DIRECTION_OUT ? RuleDirection.Out : RuleDirection.In;
                if (data.ipProtocol.HasValue)
                    entry.Protocol = (Protocol)data.ipProtocol;
                if (data.localPort.HasValue)
                    entry.LocalPort = data.localPort.Value;
                if (data.filterId.HasValue)
                {
                    entry.FilterRuntimeId = data.filterId.Value;
                    if (eventType == EventLogEvent.BLOCKED)
                        entry.FilterGroup = FilterGroups.Lookup(data.filterId.Value);
                }

                // Replace invalid IP strings with the "unspecified address" IPv6 specifier
                if (string.IsNullOrEmpty(entry.RemoteIp))
                    entry.RemoteIp = "::";
                if (string.IsNullOrEmpty(entry.LocalIp))
                    entry.LocalIp = "::";

                lock (FirewallLogEntries)
                {
                    FirewallLogEntries.Enqueue(entry);
                }

                if (eventType == EventLogEvent.BLOCKED &&
                    data.filterId.HasValue &&
                    PromptableFilterIds.Contains(data.filterId.Value) &&
                    data.direction == FwpmDirection.FWP_DIRECTION_OUT &&
                    !string.IsNullOrWhiteSpace(entry.AppPath) &&
                    !string.Equals(entry.AppPath, "System", StringComparison.OrdinalIgnoreCase) &&
                    data.localPort.HasValue &&
                    data.remotePort.HasValue &&
                    data.ipProtocol.HasValue &&
                    ((byte)data.ipProtocol.Value == (byte)Protocol.TCP ||
                        (byte)data.ipProtocol.Value == (byte)Protocol.UDP))
                {
                    var candidate = new DropCandidate(
                        new DateTimeOffset(data.timeStamp).ToUniversalTime(),
                        data.filterId.Value,
                        entry.AppPath,
                        data.packageId,
                        entry.LocalIp!,
                        entry.LocalPort,
                        entry.RemoteIp!,
                        entry.RemotePort,
                        (byte)data.ipProtocol.Value);
                    CorrelatedDrops.TryAccept(() =>
                    {
                        if (!RuntimeStopping && VisibleState.Mode == FirewallMode.Normal &&
                            PromptableFilterIds.Contains(candidate.FilterRuntimeId) && !DropCandidates.TryAdd(candidate))
                            CandidateOverflows.Record();
                    });
                }
            }
            catch (Exception exception)
            {
                Utils.LogException(exception, Utils.LOG_ID_SERVICE);
            }
        }

        // Security-log events for recent blocks. WFP net events carry no process ID, so a
        // matching audit event is what lets Network Activity name the process (and so the
        // services) behind a blocked row. The 256 newest are kept; AuditMatches' time window
        // rejects stale ones. Guarded by the FirewallLogEntries lock.
        private readonly List<ActivityAudit> RecentActivityAudits = new();
        private static readonly TimeSpan ActivityAuditWindow = TimeSpan.FromSeconds(5);

        private void LogWatcherActivityAudited(FirewallLogWatcher sender, ActivityAudit audit)
        {
            if (RuntimeStopping || audit.ProcessId == 0) return;
            lock (FirewallLogEntries)
            {
                if (RecentActivityAudits.Count >= 256)
                    RecentActivityAudits.RemoveAt(0);
                RecentActivityAudits.Add(audit);
            }
        }

        // Names the process behind each blocked row once, after its matching window has passed,
        // so audits arriving in either order within it count. Rows and audits that could pair up
        // are grouped; a group's rows take a PID only when all its audits name one process, it has
        // no more rows than audits, and that process still has the row's path and started at or
        // before the block. Two processes (several svchost.exe binding raw sockets, say), a lost
        // audit or an exited process leave the rows without one. Runs when Network Activity reads
        // the log, never on the event callbacks. Caller holds the FirewallLogEntries lock.
        private void ResolveActivityPids(DateTime settled, Dictionary<uint, (bool Open, string Path, long Creation)> identities)
        {
            var rows = new List<FirewallLogEntry>();
            bool unresolved = false;
            foreach (FirewallLogEntry entry in FirewallLogEntries)
            {
                if (entry.Event != EventLogEvent.BLOCKED) continue;
                rows.Add(entry);
                unresolved |= !entry.PidResolved && entry.Timestamp <= settled;
            }
            if (!unresolved) return;

            int n = rows.Count;
            var group = new int[n + RecentActivityAudits.Count];
            for (int i = 0; i < group.Length; ++i)
                group[i] = i;
            int Find(int i)
            {
                while (group[i] != i)
                    i = group[i] = group[group[i]];
                return i;
            }
            for (int a = 0; a < RecentActivityAudits.Count; ++a)
                for (int r = 0; r < n; ++r)
                    if (AuditMatches(RecentActivityAudits[a], rows[r]))
                        group[Find(r)] = Find(n + a);

            var rowCount = new int[group.Length];
            var auditCount = new int[group.Length];
            var pid = new uint[group.Length];
            var mixed = new bool[group.Length];
            for (int r = 0; r < n; ++r)
                ++rowCount[Find(r)];
            for (int a = 0; a < RecentActivityAudits.Count; ++a)
            {
                int root = Find(n + a);
                uint processId = RecentActivityAudits[a].ProcessId;
                if (auditCount[root]++ > 0 && pid[root] != processId)
                    mixed[root] = true;
                pid[root] = processId;
            }
            for (int r = 0; r < n; ++r)
            {
                FirewallLogEntry row = rows[r];
                if (row.PidResolved || row.Timestamp > settled) continue;
                int root = Find(r);
                row.PidResolved = true;
                // The PID must still belong to a process with the row's path that started at or
                // before the block (FILETIMEs, so the repeated daylight-saving hour can't reorder them).
                if (auditCount[root] >= rowCount[root] && auditCount[root] > 0 && !mixed[root] &&
                    IdentityOf(pid[root], identities, out string path, out long creation) &&
                    creation <= row.Timestamp.ToFileTime() &&
                    string.Equals(path, row.AppPath, StringComparison.OrdinalIgnoreCase))
                {
                    row.ProcessId = pid[root];
                    row.ProcessCreation = creation;
                }
                else
                    row.ProcessId = 0;
            }
        }

        // The service runs as LocalSystem and can open the service hosts the tray app may not.
        // One limited-rights handle per PID and read (about 5 microseconds), cached in identities.
        private static bool IdentityOf(uint pid, Dictionary<uint, (bool Open, string Path, long Creation)> identities,
            out string path, out long creation)
        {
            if (!identities.TryGetValue(pid, out var identity))
            {
                bool open = ProcessManager.TryGetProcessIdentity(pid, out string readPath, out long readCreation);
                identities[pid] = identity = (open, readPath, readCreation);
            }
            path = identity.Path;
            creation = identity.Creation;
            return identity.Open;
        }

        private static bool AuditMatches(ActivityAudit audit, FirewallLogEntry entry) =>
            audit.FilterRuntimeId == entry.FilterRuntimeId &&
            audit.Protocol == (byte)entry.Protocol &&
            audit.LocalPort == entry.LocalPort &&
            (audit.RemotePort == null || audit.RemotePort == entry.RemotePort) &&
            (audit.Direction == null ||
                entry.Direction == (audit.Direction == ConnectionDirection.Outbound ? RuleDirection.Out : RuleDirection.In)) &&
            (audit.LocalAddress == null || DropCorrelator.AddressesEqual(audit.LocalAddress, entry.LocalIp ?? string.Empty)) &&
            (audit.RemoteAddress == null || DropCorrelator.AddressesEqual(audit.RemoteAddress, entry.RemoteIp ?? string.Empty)) &&
            string.Equals(audit.ApplicationPath, entry.AppPath, StringComparison.OrdinalIgnoreCase) &&
            (audit.TimestampUtc - new DateTimeOffset(entry.Timestamp).ToUniversalTime()).Duration() <= ActivityAuditWindow;

        private void LogWatcherBlockedConnection(
            FirewallLogWatcher sender,
            BlockedConnectionAuditEvent auditEvent)
        {
            // Event callback does no SCM work and never waits for a batch to finish.
            CorrelatedDrops.TryAccept(() =>
            {
                if (RuntimeStopping || VisibleState.Mode != FirewallMode.Normal || !sender.AuditEnrichmentAvailable) return;
                if (DropCandidates.TryMatch(auditEvent, out DropCandidate? candidate) && candidate != null &&
                    PromptableFilterIds.Contains(candidate.FilterRuntimeId))
                    CorrelatedDrops.TryAdd(candidate, auditEvent);
            });
        }

        private void ResetPromptCandidates(bool stop = false, bool revokeTokens = true)
        {
            PromptCandidateLifecycle.Reset(BlockedPromptQueue, CorrelatedDrops, DropCandidates, stop, revokeTokens);
        }

        private void PromptCandidateTimerCallback(object? state)
        {
            CorrelatedDrops.Process(() => new ServicePidMap(requireStableIdentity: true),
                exception =>
                {
                    ServiceSnapshotErrors.Record();
                    if (ServiceSnapshotErrors.TryReport(DateTimeOffset.UtcNow, out long count))
                    {
                        Diagnostics.Emit(RuntimeEvent.attribution_snapshot, RuntimeResult.failure, exception.HResult);
                        Utils.Log("Service process attribution failed for " + count + " batches since the previous report.", Utils.LOG_ID_SERVICE);
                        Utils.LogException(exception, Utils.LOG_ID_SERVICE);
                    }
                },
                (candidate, audit, snapshot, unavailable) => PrepareDropCandidate(candidate, audit,
                    unavailable ? Array.Empty<string>() : (IEnumerable<string>)snapshot.GetServicesInPid(audit.ProcessId),
                    unavailable || snapshot.IsUncertain(audit.ProcessId)));
            IReadOnlyList<DropCandidate> ready;
            long generation;
            lock (BlockedPromptQueue.SyncRoot)
            {
                generation = CorrelatedDrops.Generation;
                ready = DropCandidates.DrainReady();
            }
            foreach (DropCandidate candidate in ready)
            {
                Action publish = PrepareDropCandidate(candidate, null, Array.Empty<string>());
                CorrelatedDrops.Publish(generation, publish);
            }
            ReportOverflow(CandidateOverflows, "drop candidate");
            ReportOverflow(CorrelatedDrops.Suppressed, "correlated drop (capacity or deadline)");
            if (CorrelatedDrops.Contention.TryReport(DateTimeOffset.UtcNow, out long contention))
            {
                Diagnostics.Emit(RuntimeEvent.prompt_suppression, RuntimeResult.observed);
                Utils.Log("Prompt admission skipped " + contention + " callbacks during queue lock contention. " +
                    "Connections remain blocked.", Utils.LOG_ID_SERVICE);
            }
            ReportOverflow(PromptOverflows, "prompt");
        }

        private void ReportOverflow(CoalescedDiagnostic diagnostic, string queue)
        {
            if (diagnostic.TryReport(DateTimeOffset.UtcNow, out long count))
            {
                Diagnostics.Emit(RuntimeEvent.prompt_suppression, RuntimeResult.observed);
                Utils.Log("SecureWall " + queue + " queue suppressed " + count +
                    " connections since the previous report. Connections remain blocked.", Utils.LOG_ID_SERVICE);
            }
        }

        // Called on the service command thread; callback counters use their own synchronization.
        private void RefreshAttributionState()
        {
            VisibleState.HasPassword = PasswordLock.HasPassword;
            VisibleState.Locked = PasswordLock.Locked;
            bool available = LogWatcher.AuditEnrichmentAvailable;
            Diagnostics.SetAuditAvailable(available);
            if (Diagnostics.Enabled && LastDiagnosticAuditState != (available ? 1 : 0))
            {
                LastDiagnosticAuditState = available ? 1 : 0;
                Diagnostics.Emit(RuntimeEvent.audit_health, available ? RuntimeResult.enabled : RuntimeResult.disabled,
                    available ? 0 : LogWatcher.SubscriptionHResult);
            }
            long recordErrors = LogWatcher.RecordErrors;
            if (recordErrors != LastDiagnosticRecordErrors)
            {
                LastDiagnosticRecordErrors = recordErrors;
                AuditRecordReports.Record();
            }
            if (AuditRecordReports.TryReport(DateTimeOffset.UtcNow, out _))
                Diagnostics.Emit(RuntimeEvent.audit_record_error, RuntimeResult.failure);
            long candidates = CandidateOverflows.Total + CorrelatedDrops.Suppressed.Total;
            long prompts = PromptOverflows.Total;
            if (VisibleState.AttributionAvailable == available &&
                VisibleState.DroppedPromptCandidates == candidates && VisibleState.DroppedPrompts == prompts) return;
            VisibleState.AttributionAvailable = available;
            VisibleState.DroppedPromptCandidates = candidates;
            VisibleState.DroppedPrompts = prompts;
        }

        private Action PrepareDropCandidate(
            DropCandidate candidate,
            BlockedConnectionAuditEvent? auditEvent,
            IEnumerable<string> serviceNames,
            bool snapshotUncertain = false)
        {
            if (RuntimeStopping || VisibleState.Mode != FirewallMode.Normal ||
                !PromptableFilterIds.Contains(candidate.FilterRuntimeId))
                return () => { };

            try
            {
                ServiceRegistrationStatus registration = ServiceExecutables.Lookup(
                    candidate.ApplicationPath, out IReadOnlyList<string> registeredServices);
                PromptIdentity identity = ServiceAttribution.Resolve(
                    candidate,
                    auditEvent,
                    serviceNames,
                    registration == ServiceRegistrationStatus.Registered,
                    snapshotUncertain,
                    registration == ServiceRegistrationStatus.Unknown,
                    QueryServiceSidType,
                    registeredServices,
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                if (TryQueueAlwaysAllow(identity, candidate.TimestampUtc))
                    return () => { };
                // Ignored apps stay blocked; only the prompt is skipped.
                if (IgnoredApp.AnyMatch(ActiveConfig.Service.ActiveProfile.IgnoredApps, identity))
                {
                    MarkIgnoredRow(candidate);
                    return () => { };
                }
                return () =>
                {
                    if (RuntimeStopping || VisibleState.Mode != FirewallMode.Normal ||
                        !PromptableFilterIds.Contains(candidate.FilterRuntimeId)) return;
                    PromptEnqueueResult result = BlockedPromptQueue.Enqueue(identity,
                        candidate.RemoteAddress, candidate.RemotePort, candidate.Protocol);
                    if (result.Status == PromptEnqueueStatus.CapacityReached) PromptOverflows.Record();
                };
            }
            catch (Exception exception)
            {
                Utils.LogException(exception, Utils.LOG_ID_SERVICE);
                return () => { };
            }
        }

        // Paths waiting for the worker to add an Always allow exception, with the signer the
        // check found. A later drop of the same path is neither prompted nor verified again
        // while its exception is on its way. FirstDropUtc marks the Network Activity rows the
        // exception follows.
        private readonly ConcurrentDictionary<string, (string Signer, DateTimeOffset FirstDropUtc)> AlwaysAllowQueued =
            new(StringComparer.OrdinalIgnoreCase);
        // Paths whose name matched a rule but whose signature did not, logged once each.
        private readonly ConcurrentDictionary<string, byte> AlwaysAllowMismatchLogged = new(StringComparer.OrdinalIgnoreCase);
        // Files that failed the check (or whose exception could not be added), by size and
        // write time, so a retrying app is prompted without hashing the file on every drop.
        // An entry counts only while the configuration it was checked against is still active:
        // a rule added or removed since then gets a fresh check.
        private readonly ConcurrentDictionary<string, (long Length, DateTime WriteUtc, DateTime UntilUtc, ServerConfiguration Config)> AlwaysAllowRejected =
            new(StringComparer.OrdinalIgnoreCase);

        // Runs on the prompt timer thread outside the queue lock (signature checks are slow).
        // True when the drop is handled by an Always allow rule and must not become a prompt.
        // Any doubt (name only, bad signature, an existing rule for the path, full request
        // queue) leaves it to the prompt.
        private bool TryQueueAlwaysAllow(PromptIdentity identity, DateTimeOffset dropUtc)
        {
            // A file that is also a service's image is always prompted: its rule covers those services.
            if (identity.Kind != PromptIdentityKind.Executable || !identity.CanAllow ||
                identity.CoveredServiceNames.Count != 0 ||
                identity.ExecutablePath is not string path || !PromptAllowPolicy.RequiresExistenceCheck(path))
                return false;
            ServerConfiguration config = ActiveConfig.Service;
            List<AlwaysAllowRule> rules = config.ActiveProfile.AlwaysAllowRules;
            if (!rules.Exists(rule => rule != null && rule.NameMatches(path)))
                return false;
            if (AlwaysAllowQueued.ContainsKey(path))
                return true;
            if (!SignedFileIdentity.IsLocalFixedPath(path))
                return false;
            // An existing exception (a block, or an allow the owner narrowed) is never widened silently.
            if (HasExceptionForSubject(new ExecutableSubject(path)) || WasRejected(path, config))
                return false;

            SignedFileIdentity? signer = SignedFileIdentity.TryRead(path);
            if (signer == null || !rules.Exists(rule => rule != null && rule.Matches(path, signer.Subject)))
            {
                RememberRejected(path, config);
                if (AlwaysAllowMismatchLogged.Count < 64 && AlwaysAllowMismatchLogged.TryAdd(path, 0))
                    Utils.Log("Always allow did not apply to " + path + ": its name matches a rule, but it is not a file on a local fixed drive with a valid embedded signature from that rule's signer. It is prompted instead.", Utils.LOG_ID_SERVICE);
                return false;
            }

            if (!AlwaysAllowQueued.TryAdd(path, (signer.Subject, dropUtc)))
                return true;
            // Never block here: the worker may need the prompt queue lock this thread's caller holds next.
            if (!Q.TryAdd(new TwRequest(TwMessageSimple.CreateRequest(MessageType.AUTO_ALLOW_SIGNED))))
            {
                AlwaysAllowQueued.TryRemove(path, out _);
                return false;
            }
            return true;
        }

        // Worker thread. Adds the same outbound-only exception as Allow in the popup for every
        // queued path whose rule still exists, in one policy change.
        private void ApplyQueuedAlwaysAllows()
        {
            KeyValuePair<string, (string Signer, DateTimeOffset FirstDropUtc)>[] queued = AlwaysAllowQueued.ToArray();
            if (queued.Length == 0)
                return;
            string[] paths = queued.Select(entry => entry.Key).ToArray();
            try
            {
                if (VisibleState.Mode != FirewallMode.Normal || RuntimeSessionRevoked)
                    return;
                var added = new List<FirewallExceptionV3>();
                foreach (KeyValuePair<string, (string Signer, DateTimeOffset FirstDropUtc)> entry in queued)
                {
                    string path = entry.Key;
                    // The rule must still trust the signer the check found, not just the name.
                    if (!SignedFileIdentity.IsLocalFixedPath(path) || !File.Exists(path) ||
                        !ActiveConfig.Service.ActiveProfile.AlwaysAllowRules.Exists(rule => rule != null && rule.Matches(path, entry.Value.Signer)))
                        continue;
                    var subject = new ExecutableSubject(path);
                    if (HasExceptionForSubject(subject))
                        continue;
                    added.Add(new FirewallExceptionV3(subject, new TcpUdpPolicy
                    {
                        AllowedRemoteTcpConnectPorts = "*",
                        AllowedRemoteUdpConnectPorts = "*",
                    }));
                }
                if (added.Count == 0)
                    return;
                var candidate = Utils.DeepClone(ActiveConfig.Service);
                candidate.ActiveProfile.AddExceptions(added);
                ApplyConfiguration(candidate, VisibleState.Mode);
                foreach (FirewallExceptionV3 exception in added)
                    Utils.Log("Always allow added an outbound rule for " + ((ExecutableSubject)exception.Subject).ExecutablePath + ".", Utils.LOG_ID_SERVICE);
                MarkAlwaysAllowedRows(queued.Where(entry => added.Exists(exception =>
                    string.Equals(((ExecutableSubject)exception.Subject).ExecutablePath, entry.Key, StringComparison.OrdinalIgnoreCase))));
            }
            catch (Exception exception)
            {
                // Prompt these paths for a while instead of retrying the same failing change on every drop.
                ServerConfiguration config = ActiveConfig.Service;
                foreach (string path in paths)
                    RememberRejected(path, config);
                Utils.LogException(exception, Utils.LOG_ID_SERVICE);
            }
            finally
            {
                foreach (string path in paths)
                    AlwaysAllowQueued.TryRemove(path, out _);
            }
        }

        // Network Activity shows the default-deny drops an Always allow exception now covers as
        // "Blocked, then allowed by Always allow" instead of plain blocks.
        private void MarkAlwaysAllowedRows(IEnumerable<KeyValuePair<string, (string Signer, DateTimeOffset FirstDropUtc)>> allowed)
        {
            lock (FirewallLogEntries)
            {
                foreach (KeyValuePair<string, (string Signer, DateTimeOffset FirstDropUtc)> path in allowed)
                {
                    foreach (FirewallLogEntry entry in FirewallLogEntries)
                    {
                        // The exception allows outbound TCP and UDP only.
                        if (entry.Event == EventLogEvent.BLOCKED && entry.FilterGroup == FilterGroup.DefaultAction &&
                            entry.Direction == RuleDirection.Out && (entry.Protocol == Protocol.TCP || entry.Protocol == Protocol.UDP) &&
                            string.Equals(entry.AppPath, path.Key, StringComparison.OrdinalIgnoreCase) &&
                            new DateTimeOffset(entry.Timestamp).ToUniversalTime() >= path.Value.FirstDropUtc)
                            entry.AllowedByAlwaysAllow = true;
                    }
                }
            }
        }

        // Network Activity shows a drop whose prompt the Ignored list skipped as "Blocked (ignored)".
        // The candidate was built from one row, so only that row is marked: matching by path
        // alone would also mark other services sharing svchost.exe.
        private void MarkIgnoredRow(DropCandidate candidate)
        {
            lock (FirewallLogEntries)
            {
                foreach (FirewallLogEntry entry in FirewallLogEntries)
                {
                    if (entry.Event == EventLogEvent.BLOCKED && entry.FilterRuntimeId == candidate.FilterRuntimeId &&
                        entry.Direction == RuleDirection.Out && (byte)entry.Protocol == candidate.Protocol &&
                        entry.LocalPort == candidate.LocalPort && entry.RemotePort == candidate.RemotePort &&
                        string.Equals(entry.RemoteIp, candidate.RemoteAddress, StringComparison.Ordinal) &&
                        string.Equals(entry.AppPath, candidate.ApplicationPath, StringComparison.OrdinalIgnoreCase) &&
                        new DateTimeOffset(entry.Timestamp).ToUniversalTime() == candidate.TimestampUtc)
                        entry.PromptIgnored = true;
                }
            }
        }

        // Any rule for the path, including those an enabled special profile (Windows Defender,
        // for example) installs for it.
        private bool HasExceptionForSubject(ExecutableSubject subject) =>
            CommittedRuleExecutables.Contains(subject.ExecutablePath) ||
            ActiveConfig.Service.ActiveProfile.AppExceptions.Exists(existing => existing.Subject.Equals(subject));

        // Both run only after IsLocalFixedPath accepted the path.
        private bool WasRejected(string path, ServerConfiguration config)
        {
            if (!AlwaysAllowRejected.TryGetValue(path, out var rejected))
                return false;
            try
            {
                var file = new FileInfo(path);
                if (ReferenceEquals(rejected.Config, config) && DateTime.UtcNow < rejected.UntilUtc && file.Exists &&
                    file.Length == rejected.Length && file.LastWriteTimeUtc == rejected.WriteUtc)
                    return true;
            }
            catch (Exception) { }
            AlwaysAllowRejected.TryRemove(path, out _);
            return false;
        }

        private void RememberRejected(string path, ServerConfiguration config)
        {
            try
            {
                if (!SignedFileIdentity.IsLocalFixedPath(path))
                    return;
                var file = new FileInfo(path);
                if (!file.Exists)
                    return;
                if (AlwaysAllowRejected.Count >= 256)
                    AlwaysAllowRejected.Clear();
                AlwaysAllowRejected[path] = (file.Length, file.LastWriteTimeUtc, DateTime.UtcNow.AddMinutes(10), config);
            }
            catch (Exception) { }
        }

        // Null when SCM cannot report the SID type; attribution then keeps Allow disabled.
        // Runs per drop candidate, so failures are coalesced to one log line per minute.
        private uint? QueryServiceSidType(string serviceName)
        {
            try
            {
                using var scm = new ServiceControlManager();
                return scm.GetServiceSidType(serviceName);
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                ServiceSidQueryErrors.Record();
                if (ServiceSidQueryErrors.TryReport(DateTimeOffset.UtcNow, out long count))
                    Utils.Log("Could not read the service SID type " + count + " times since the previous report; latest: " +
                        serviceName + " (error " + exception.NativeErrorCode + "). Those prompts stay non-allowable.",
                        Utils.LOG_ID_SERVICE);
                return null;
            }
        }

        // Entry point for thread that listens to commands from the controller application.
        private TwMessage PipeServerDataReceived(TwMessage reqMsg, uint clientProcessId)
        {
            if (((int)reqMsg.Type > 2047) && PasswordLock.Locked)
            {
                if (reqMsg.Type == MessageType.ALLOW_PROMPT || reqMsg.Type == MessageType.ALLOW_PROMPT_ALWAYS ||
                    reqMsg.Type == MessageType.DISMISS_PROMPT)
                    Diagnostics.Emit(reqMsg.Type == MessageType.DISMISS_PROMPT ? RuntimeEvent.prompt_ignore : RuntimeEvent.prompt_allow,
                        RuntimeResult.locked);
                // Notify that we need to be unlocked first
                return TwMessageLocked.Instance;
            }
            if (((int)reqMsg.Type > 4095))
            {
                // We cannot receive this from the client
                return TwMessageError.Instance;
            }
            else
            {
                // Process and wait for response
                var req = new TwRequest(reqMsg) { ClientProcessId = clientProcessId };
                Q.Add(req);

                // Background reads, popup timeouts and prompt actions on unknown or expired
                // tokens never extend the password inactivity window.
                TwMessage response = req.Response;
                ControllerActivity.Record(ControllerActivityPolicy.IsUserActivity(
                    reqMsg.Type, response.Type, (response as TwMessagePromptAction)?.Status));
                return response;
            }
        }

        public void RequestStop()
        {
            var req = new TwRequest(TwMessageSimple.CreateRequest(MessageType.STOP_SERVICE));
            if (!Q.TryAdd(req, TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The firewall worker did not accept the stop request.");
        }

        public void DisplayPowerEvent(bool turnOn)
        {
            Q.Add(new TwRequest(TwMessageDisplayPowerEvent.CreateRequest(turnOn)));
        }

        public void MountedVolumesChangedEvent()
        {
            RuleReloadEventMerger.Pulse();
        }

        public void Dispose()
        {
            using var timer = new HierarchicalStopwatch("TinyWallService.Dispose()");
            // Withdraw permits before any cleanup operation that may throw or wait for another thread.
            RuntimeStopping = true;
            ResetPromptCandidates(stop: true);
            DisposeRuntimeSubscription();
            WfpEngine.Dispose();
            foreach (UpdateNetworkLease lease in UpdateLeases.Clear())
                lease.Process?.Dispose();
            PromptableFilterIds.Replace(Array.Empty<ulong>());
            PortBlocklistFilterIds.Clear();
            FilterGroups.Clear();
            PortBlocklistDiagnosticsAvailable = true;
            EffectivePortBlocklist = false;
            ServerPipe?.Dispose();
            ProcessStartWatcher.EventArrived -= ProcessStartWatcher_EventArrived;
            try { ProcessStartWatcher.Stop(); } catch { }
            ProcessStartWatcher.Dispose();

            if (MinuteTimer != null)
            {
                using WaitHandle wh = new AutoResetEvent(false);
                MinuteTimer.Dispose(wh);
                wh.WaitOne();
            }

            if (PromptCandidateTimer != null)
            {
                using WaitHandle wh = new AutoResetEvent(false);
                PromptCandidateTimer.Dispose(wh);
                wh.WaitOne();
            }

            RuleReloadEventMerger.Dispose();
            LocalSubnetFilterConditions.Dispose();
            GatewayFilterConditions.Dispose();
            DnsFilterConditions.Dispose();
            LogWatcher.BlockedConnection -= LogWatcherBlockedConnection;
            LogWatcher.ActivityAudited -= LogWatcherActivityAudited;
            LogWatcher.Dispose();
            HostsFileManager.Dispose();
            FileLocker.UnlockAll();

            FirewallThreadThrottler?.Dispose();
            Q.Dispose();

            // Dynamic filters disappear when WfpEngine closes. Recovery baseline is removed only by uninstall.
            PathMapper.Instance.Dispose();
        }
    }


    internal sealed class TinyWallService : ServiceBase
    {
        internal readonly static string[] ServiceDependencies = new string[]
        {
            "Schedule",
            "Winmgmt",
            "MpsSvc",
            "BFE"
        };

        internal const string SERVICE_NAME = "SecureWall";
        internal const string SERVICE_DISPLAY_NAME = "SecureWall Service";

        private TinyWallServer? Server;
        private ServiceRuntimeDiagnostics? RuntimeDiagnostics;
        private Thread? FirewallWorkerThread;
        private volatile bool StopRequested;
#if !DEBUG
        private bool IsComputerShuttingDown;
#endif
        internal TinyWallService()
            : base()
        {
            this.AcceptedControls = ServiceAcceptedControl.SERVICE_ACCEPT_SHUTDOWN;
            this.AcceptedControls |= ServiceAcceptedControl.SERVICE_ACCEPT_POWEREVENT;
            this.AcceptedControls |= ServiceAcceptedControl.SERVICE_ACCEPT_STOP;
        }

        public override string ServiceName
        {
            get { return SERVICE_NAME; }
        }

        private void FirewallWorkerMethod()
        {
            using var diagnostics = new ServiceRuntimeDiagnostics();
            RuntimeDiagnostics = diagnostics;
            diagnostics.Emit(RuntimeEvent.service_start, RuntimeResult.attempt);
            Exception? failure = null;
            try
            {
                Installer.UpgradeFault.Trip("service-start-fail");
                using (Server = new TinyWallServer(diagnostics))
                {
                    Server.Run(this);
                }
                diagnostics.Emit(RuntimeEvent.service_shutdown, RuntimeResult.success);
            }
            catch (Exception exception)
            {
                // Run returns normally only for a requested stop. Any exception is a startup
                // failure or an exhausted runtime rebuild; report it instead of crashing the thread.
                failure = exception;
                diagnostics.Emit(RuntimeEvent.service_failure, RuntimeResult.failure, exception.HResult);
                try { Utils.LogException(exception, Utils.LOG_ID_SERVICE); } catch { }
#if DEBUG
                throw;
#endif
            }
            finally
            {
                // Server disposal/revocation has already completed or failed. No policy lock
                // is held here; a stuck diagnostic disk gets at most 250 ms before exit.
                diagnostics.FinishShutdown();
#if !DEBUG
                Thread.MemoryBarrier();
                try
                {
                    if (!StopRequested && !IsComputerShuttingDown)    // Normal stop completion belongs to StopServer.
                    {
                        // A nonzero exit code lets SCM run its non-crash failure actions.
                        var (win32ExitCode, serviceExitCode) = ServiceLifecyclePolicy.StoppedStatus(failure != null, Server?.Ready == true);
                        SetServiceStateReached(ServiceState.Stopped, win32ExitCode, serviceExitCode);
                    }
                }
                finally
                {
                    if (!StopRequested)
                        Process.GetCurrentProcess().Kill();
                }
#endif
            }
        }

        // Entry point for Windows service.
        protected override void OnStart(string[] args)
        {
            // Initialization on a new thread prevents stalling the SCM
            StopRequested = false;
            RequestAdditionalTime(120000);
            FirewallWorkerThread = new Thread(new ThreadStart(FirewallWorkerMethod)) { Name = "ServiceMain" };
            FirewallWorkerThread.Start();
        }

        private void StopServer()
        {
            RuntimeDiagnostics?.Emit(RuntimeEvent.service_stop_requested, RuntimeResult.attempt);
            StopRequested = true;
            Thread.MemoryBarrier();
            var clock = Stopwatch.StartNew();
            WorkerStopCoordinator.Stop(
                () => Server?.RequestStop(),
                timeout => FirewallWorkerThread == null || FirewallWorkerThread.Join(timeout),
                RequestAdditionalTime,
                () => FinishStateChange(),
                error => Environment.FailFast("SecureWall could not finish stopping; withdrawing runtime permissions by terminating the service process.", error),
                () => clock.Elapsed);
        }

        // Executed when service is stopped manually.
        protected override void OnStop()
        {
            StopServer();
        }

        // Executed on computer shutdown.
        protected override void OnShutdown()
        {
            RuntimeDiagnostics?.Emit(RuntimeEvent.service_stop_requested, RuntimeResult.attempt);
#if !DEBUG
            IsComputerShuttingDown = true;
#endif
            StartStateChange(ServiceState.StopPending);
        }

        protected override void OnDeviceEvent(DeviceEventData data)
        {
            if ((data.Event == DeviceEventType.DeviceArrival) || (data.Event == DeviceEventType.DeviceRemoveComplete))
            {
                bool pathMapperRebuildNeeded = false;

                if (data.DeviceType == DeviceBroadcastHdrDevType.DBT_DEVTYP_DEVICEINTERFACE)
                {
                    if (data.Class == DeviceInterfaceClass.GUID_DEVINTERFACE_VOLUME)
                    {
                        pathMapperRebuildNeeded = true;
                    }
                }
                else if (data.DeviceType == DeviceBroadcastHdrDevType.DBT_DEVTYP_VOLUME)
                {
                    pathMapperRebuildNeeded = true;
                }

                if (pathMapperRebuildNeeded)
                {
                    Server?.MountedVolumesChangedEvent();
                }
            }
        }

        protected override void OnPowerEvent(PowerEventData data)
        {
            if (data.Event == PowerEventType.PowerSettingChange)
            {
                if (data.Setting == PowerSetting.GUID_CONSOLE_DISPLAY_STATE)
                {
                    if (data.PayloadInt == 0)
                        Server?.DisplayPowerEvent(false);
                    else if (data.PayloadInt == 1)
                        Server?.DisplayPowerEvent(true);
                    else
                    {
                        // Dimming event... ignore
                    }
                }
            }
        }
    }
}
