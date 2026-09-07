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
        private enum FilterWeights : ulong
        {
            Blocklist = 9000000,
            RawSocketPermit = 8000000,
            RawSocketBlock = 7000000,
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

        // Context needed for learning mode
        private readonly FirewallLogWatcher LogWatcher;
        private readonly List<FirewallExceptionV3> LearningNewExceptions = new();

        // Only runtime IDs belonging to outbound default-block filters may create prompts.
        private readonly PromptableFilterSet PromptableFilterIds = new();
        private readonly PromptQueue BlockedPromptQueue = new(SystemClock.Instance);
        private readonly DropCandidateBuffer DropCandidates = new(SystemClock.Instance);
        private readonly ServiceExecutableCatalog ServiceExecutables = new();

        // Context for auto rule inheritance
        private readonly object InheritanceGuard = new();
        private HashSet<string> UserSubjectExes = new(StringComparer.OrdinalIgnoreCase);        // All executables with pre-configured rules.
        private Dictionary<string, List<FirewallExceptionV3>> ChildInheritance = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, HashSet<string>> ChildInheritedSubjectExes = new(StringComparer.OrdinalIgnoreCase);   // Executables that have been already auto-whitelisted due to inheritance
        private readonly ThreadThrottler FirewallThreadThrottler = new(Thread.CurrentThread, ThreadPriority.Highest, false);
        private StringBuilder? ProcessStartWatcher_Sbuilder;

        private bool RunService = false;
        private bool DisplayCurrentlyOn = true;
        private readonly ServerState VisibleState = new();

        private readonly Engine WfpEngine = new("SecureWall Session", "", FWPM_SESSION_FLAGS.FWPM_SESSION_FLAG_DYNAMIC, 5000);
        private IDisposable? RuntimeEventSubscription;
        private volatile bool RuntimeStopping;
        private bool RuntimeSessionRevoked;
        private readonly List<Guid> RuntimeFilterKeys = new();
        private List<Guid>? PendingRuntimeFilterKeys;
        private ServerConfiguration? ApplyingConfiguration;
        private FirewallMode? ApplyingMode;
        private bool BaselineInstalled;
        private ServerConfiguration PolicyConfiguration => ApplyingConfiguration ?? ActiveConfig.Service;
        private FirewallMode PolicyMode => ApplyingMode ?? VisibleState.Mode;
        private readonly ManagementEventWatcher ProcessStartWatcher = new(new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
        private readonly EventMerger RuleReloadEventMerger = new(1000);

        private HashSet<IpAddrMask> LocalSubnetAddreses = new();
        private HashSet<IpAddrMask> GatewayAddresses = new();
        private HashSet<IpAddrMask> DnsAddresses = new();
        private readonly FilterConditionList LocalSubnetFilterConditions = new();
        private readonly FilterConditionList GatewayFilterConditions = new();
        private readonly FilterConditionList DnsFilterConditions = new();

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
                case FirewallMode.Learning:
                    {
                        // Add rule to explicitly allow everything
                        var def = new RuleDef(ModeId, "Allow everything", GlobalSubject.Instance, RuleAction.Allow, RuleDirection.InOut, Protocol.Any, (ulong)FilterWeights.DefaultPermit);
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
                        if (ex.ChildProcessesInherit)
                        {
                            // We might have multiple rules with the same exePath, so we maintain a list of exceptions
                            if (!ChildInheritance.ContainsKey(exePath))
                                ChildInheritance.Add(exePath, new List<FirewallExceptionV3>());
                            ChildInheritance[exePath].Add(ex);
                        }
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

                    foreach (var pair in procTree)
                    {
                        pidsChecked.Clear();

                        string procPath = pair.Value.ImagePath;

                        // Skip if we have no path
                        if (string.IsNullOrEmpty(procPath))
                            continue;

                        // Skip if we have a user-defined rule for this path
                        if (UserSubjectExes.Contains(procPath))
                            continue;

                        // Start walking up the process tree
                        for (var parentEntry = procTree[pair.Key]; ;)
                        {
                            long childCreationTime = parentEntry.CreationTime;
                            if (procTree.TryGetValue(parentEntry.ParentProcessId, out var val))
                                parentEntry = val;
                            else
                                // We reached top of process tree (with non-existing parent)
                                break;

                            // Check if what we have is really the parent, or just a reused PID
                            if (parentEntry.CreationTime > childCreationTime)
                                // We reached the top of the process tree (with non-existing parent)
                                break;

                            if (parentEntry.ProcessId == 0)
                                // We reached top of process tree (with idle process)
                                break;

                            if (pidsChecked.Contains(parentEntry.ProcessId))
                                // We've been here before, damn it. Avoid looping eternally...
                                break;

                            pidsChecked.Add(parentEntry.ProcessId);

                            if (string.IsNullOrEmpty(parentEntry.ImagePath))
                                // We cannot get the path, so let's skip this parent
                                continue;

                            if (ChildInheritedSubjectExes.TryGetValue(procPath, out var childVal))
                            {
                                if (childVal.Contains(parentEntry.ImagePath))
                                    // We have already processed this parent-child combination
                                    break;
                            }

                            if (ChildInheritance.TryGetValue(parentEntry.ImagePath, out List<FirewallExceptionV3> exList))
                            {
                                var subj = new ExecutableSubject(procPath);
                                foreach (var userEx in exList)
                                    if (!EnforcementPolicy.IsExpired(userEx.CreationDate, (int)userEx.Timer, DateTime.Now))
                                        GetRulesForException(new FirewallExceptionV3(subj, userEx.Policy), rules, rawSocketExceptions, (ulong)FilterWeights.UserPermit, (ulong)FilterWeights.UserBlock);

                                if (!ChildInheritedSubjectExes.ContainsKey(procPath))
                                    ChildInheritedSubjectExes.Add(procPath, new HashSet<string>());
                                ChildInheritedSubjectExes[procPath].Add(parentEntry.ImagePath);
                                break;
                            }
                        }
                    }
                }   // if (ChildInheritance ...
            }

            // Convert all paths to kernel-format
            foreach (var r in rules)
            {
                if (r.Application is not null)
                    r.Application = PathMapper.Instance.ConvertPathIgnoreErrors(r.Application, PathFormat.NativeNt);
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
            var promptableFilterIds = new List<ulong>();
            Transaction? trx = useTransaction ? WfpEngine.BeginTransaction() : null;
            var addedKeys = useTransaction ? new List<Guid>() : PendingRuntimeFilterKeys;
            if (useTransaction)
                PendingRuntimeFilterKeys = addedKeys;
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

                // Built-in protections
                if (PolicyMode != FirewallMode.Disabled)
                {
                    InstallRawSocketPermits(rawSocketExceptions);
                    InstallWsl2Filters(EnforcementPolicy.OptionalPermitEnabled(PolicyMode == FirewallMode.BlockAll, PolicyConfiguration.ActiveProfile.HasSpecialException("WSL_2")));
                }

                trx?.Commit();
                if (useTransaction)
                    RuntimeFilterKeys.AddRange(addedKeys!);
                return promptableFilterIds;
            }
            finally
            {
                if (useTransaction)
                    PendingRuntimeFilterKeys = null;
                trx?.Dispose();
            }

        }

        private void InstallFirewallRules()
        {
            using var timer = new HierarchicalStopwatch("InstallFirewallRules()");
            PathMapper.Instance.RebuildCache();
            lock (InheritanceGuard)
            {
                var previousSubjects = UserSubjectExes;
                var previousInheritance = ChildInheritance;
                var previousInheritedSubjects = ChildInheritedSubjectExes;
                bool committed = false;
                UserSubjectExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ChildInheritance = new Dictionary<string, List<FirewallExceptionV3>>();
                ChildInheritedSubjectExes = new Dictionary<string, HashSet<string>>();
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
                    if (PolicyMode != FirewallMode.Disabled)
                    {
                        InstallPortScanProtection();
                        InstallRawSocketBlocks();
                    }
                    List<ulong> newPromptableFilterIds = InstallRules(rules, rawSocketExceptions, false);
                    trx.Commit();
                    committed = true;
                    RuntimeFilterKeys.Clear();
                    RuntimeFilterKeys.AddRange(candidateKeys);
                    PromptableFilterIds.Replace(PolicyMode == FirewallMode.Normal
                        ? newPromptableFilterIds : Array.Empty<ulong>());
                    LastRuleReloadTime = DateTime.Now;
                }
                finally
                {
                    PendingRuntimeFilterKeys = null;
                    if (!committed)
                    {
                        UserSubjectExes = previousSubjects;
                        ChildInheritance = previousInheritance;
                        ChildInheritedSubjectExes = previousInheritedSubjects;
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
            if (BaselineInstalled)
                return;
            using var baseline = new Engine("SecureWall Recovery Baseline", "", FWPM_SESSION_FLAGS.None, 5000);
            using var transaction = baseline.BeginTransaction();
            // Atomically replace legacy persisted permits with a restrictive recovery policy.
            DeleteWfpObjects(baseline, true);

            // Install provider
            var provider = new FWPM_PROVIDER0();
            provider.displayData.name = "SecureWall";
            provider.displayData.description = "SecureWall Provider";
            provider.serviceName = TinyWallService.SERVICE_NAME;
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

        private IReadOnlyList<ulong> InstallWfpFilter(Filter f, bool required = true)
        {
            return WfpFilterPairRegistration.Register(lifetime =>
            {
                f.FilterKey = Guid.NewGuid();
                f.Flags = 0;
                WfpEngine.RegisterFilter(f);
                (PendingRuntimeFilterKeys ?? throw new InvalidOperationException("No runtime transaction is active.")).Add(f.FilterKey);
                return f.FilterId;
            }, required, runtimeOnly: true);
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
                PromptFilterClassifier.IsRequiredProtection(r.Action == RuleAction.Block));
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

            InstallWfpFilter(f);
        }

        private void InstallWsl2Filters(bool permit)
        {
            const string ifAlias = "vEthernet (WSL)";
            if (LocalInterfaceCondition.InterfaceAliasExists(ifAlias))
            {
                InstallWsl2Filters(permit, ifAlias, LayerKeyEnum.FWPM_LAYER_ALE_AUTH_CONNECT_V4);
                InstallWsl2Filters(permit, ifAlias, LayerKeyEnum.FWPM_LAYER_ALE_AUTH_CONNECT_V6);
                InstallWsl2Filters(permit, ifAlias, LayerKeyEnum.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4);
                InstallWsl2Filters(permit, ifAlias, LayerKeyEnum.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6);
                InstallWsl2Filters(permit, ifAlias, LayerKeyEnum.FWPM_LAYER_OUTBOUND_ICMP_ERROR_V4);
                InstallWsl2Filters(permit, ifAlias, LayerKeyEnum.FWPM_LAYER_OUTBOUND_ICMP_ERROR_V6);
                InstallWsl2Filters(permit, ifAlias, LayerKeyEnum.FWPM_LAYER_INBOUND_ICMP_ERROR_V4);
                InstallWsl2Filters(permit, ifAlias, LayerKeyEnum.FWPM_LAYER_INBOUND_ICMP_ERROR_V6);
            }
        }

        private void InstallWsl2Filters(bool permit, string ifAlias, LayerKeyEnum layer)
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
            f.Conditions.Add(new LocalInterfaceCondition(ifAlias));

            InstallWfpFilter(f);
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

                InstallWfpFilter(f);
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

            InstallWfpFilter(f);
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

        private static void RestoreInterruptedPolicy()
        {
            bool recoveryExists;
            try
            {
                File.GetAttributes(ConfigRecoveryPath);
                recoveryExists = true;
            }
            catch (FileNotFoundException) { recoveryExists = false; }
            catch (DirectoryNotFoundException) { recoveryExists = false; }
            PolicyRecoveryJournal.Recover(recoveryExists,
                () => ServerConfiguration.Load(ConfigRecoveryPath).Save(ConfigSavePath),
                () => File.Delete(ConfigRecoveryPath));
        }

        private static ServerConfiguration LoadServerConfig()
        {
            try
            {
                return ServerConfiguration.Load(ConfigSavePath);
            }
            catch { }

            // Load from file failed, prepare default config instead
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
        private void InitFirewall()
        {
            try { InitializeFirewallPolicy(); }
            catch
            {
                // Initialization/reinitialization expires until-reboot grants too; a
                // persistence failure cannot roll those permissions back into service.
                FailClosed();
                throw;
            }
        }

        private void InitializeFirewallPolicy()
        {
            using var timer = new HierarchicalStopwatch("InitFirewall()");
            EnsureRestrictiveBaseline();
            RestoreInterruptedPolicy();
            LoadDatabase();
            ServerConfiguration candidate = LoadServerConfig();
            if (candidate.StartupMode < FirewallMode.Normal || candidate.StartupMode > FirewallMode.AllowOutgoing)
                candidate.StartupMode = FirewallMode.Normal;
            PruneExpiredRules(candidate, restarting: true);
            if (ActiveConfig.Service == null)
            {
                // Initial state is not served until Run has committed protection and reported readiness.
                ActiveConfig.Service = candidate;
                VisibleState.Mode = candidate.StartupMode;
            }
            lock (LearningNewExceptions)
            {
                candidate.ActiveProfile.AddExceptions(LearningNewExceptions.Select(item => Utils.DeepClone(item)).ToList());
                ApplyConfiguration(candidate, candidate.StartupMode);
                LearningNewExceptions.Clear();
            }
        }


        // This method reapplies all firewall settings.
        private void ReapplySettings()
        {
            using var timer = new HierarchicalStopwatch("ReapplySettings()");
            HostsFileManager.EnableProtection = PolicyConfiguration.LockHostsFile;
            if (PolicyConfiguration.Blocklists.EnableBlocklists
                && PolicyConfiguration.Blocklists.EnableHostsBlocklist)
                HostsFileManager.EnableHostsFile();
            else
                HostsFileManager.DisableHostsFile();
        }

        private static void LoadDatabase()
        {
            using var timer = new HierarchicalStopwatch("LoadDatabase()");

            try
            {
                GlobalInstances.AppDatabase = DatabaseClasses.AppDatabase.Load();
            }
            catch
            {
                GlobalInstances.AppDatabase = new DatabaseClasses.AppDatabase();
            }
        }

        private DateTime? LastUpdateCheck_ = null;
        private const string LastUpdateCheck_FILENAME = "updatecheck";
        private DateTime LastUpdateCheck
        {
            get
            {
                if (!LastUpdateCheck_.HasValue)
                {
                    try
                    {
                        string filePath = Path.Combine(Utils.AppDataPath, LastUpdateCheck_FILENAME);
                        if (File.Exists(filePath))
                        {
                            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                            using var sr = new StreamReader(fs, Encoding.UTF8);
                            LastUpdateCheck_ = DateTime.Parse(sr.ReadLine());
                        }
                    }
                    catch { }
                }

                if (!LastUpdateCheck_.HasValue)
                    LastUpdateCheck_ = DateTime.MinValue;
                if (LastUpdateCheck_.Value > DateTime.Now)
                    LastUpdateCheck_ = DateTime.MinValue;

                return LastUpdateCheck_.Value;
            }

            set
            {
                LastUpdateCheck_ = value;

                try
                {
                    string filePath = Path.Combine(Utils.AppDataPath, LastUpdateCheck_FILENAME);
                    using var afu = new AtomicFileUpdater(filePath);
                    using (var fs = new FileStream(afu.TemporaryFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        using var sw = new StreamWriter(fs, Encoding.UTF8);
                        sw.WriteLine(value.ToString("O"));
                    }
                    afu.Commit();
                }
                catch { }
            }
        }

        private void UpdaterMethod()
        {
            if (!SecureWallProduct.UpdateFeedEnabled)
                return;

            // This is an automatic update check in the background.
            // If we fail (for whatever reason, no internet, server down etc.), do it silently.
            UpdateDescriptor? update = null;
            try { update = UpdateChecker.GetDescriptor(); }
            catch { return; }
            if (update is null)
                return;

            VisibleState.Update = update;
            GlobalInstances.ServerChangeset = Guid.NewGuid();

            try
            {
                var hostsUpdate = update.GetModule(UpdateDescriptor.MODULE_NAME_HOSTS);
                if (hostsUpdate is not null)
                {
                    if (!string.Equals(hostsUpdate.DownloadHash, HostsFileManager.GetHostsHash(), StringComparison.OrdinalIgnoreCase))
                        GetCompressedUpdate(hostsUpdate, HostsUpdateInstall);
                }

                var databaseUpdate = update.GetModule(UpdateDescriptor.MODULE_NAME_DATABASE);
                if (databaseUpdate is not null)
                {
                    if (!string.Equals(databaseUpdate.DownloadHash, Hasher.HashFile(DatabaseClasses.AppDatabase.DBPath), StringComparison.OrdinalIgnoreCase))
                        GetCompressedUpdate(databaseUpdate, DatabaseUpdateInstall);
                }
            }
            catch (Exception e)
            {
                Utils.LogException(e, Utils.LOG_ID_SERVICE);
            }
        }

        private static void GetCompressedUpdate(UpdateModule module, WaitCallback installMethod)
        {
            string tmpCompressedPath = Path.GetTempFileName();
            string tmpFile = Path.GetTempFileName();
            try
            {
                using (var downloader = new WebClient())
                {
                    downloader.DownloadFile(module.UpdateURL, tmpCompressedPath);
                }
                Utils.DecompressDeflate(tmpCompressedPath, tmpFile);

                if (Hasher.HashFile(tmpFile).Equals(module.DownloadHash, StringComparison.OrdinalIgnoreCase))
                {
#if !DEBUG  // don't install anything during debug
                    installMethod(tmpFile);
#endif
                }
            }
            catch { }
            finally
            {
                try
                {
                    File.Delete(tmpCompressedPath);
                }
                catch { }

                try
                {
                    File.Delete(tmpFile);
                }
                catch { }
            }
        }

        private void HostsUpdateInstall(object file)
        {
            string tmpHostsPath = (string)file;
            HostsFileManager.UpdateHostsFile(tmpHostsPath);

            if (ActiveConfig.Service.Blocklists.EnableBlocklists
                && ActiveConfig.Service.Blocklists.EnableHostsBlocklist)
            {
                HostsFileManager.EnableHostsFile();
            }
        }
        private void DatabaseUpdateInstall(object file)
        {
            string tmpFilePath = (string)file;

            FileLocker.Unlock(DatabaseClasses.AppDatabase.DBPath);
            using (var afu = new AtomicFileUpdater(DatabaseClasses.AppDatabase.DBPath))
            {
                File.Copy(tmpFilePath, afu.TemporaryFilePath, true);
                afu.Commit();
            }
            FileLocker.Lock(DatabaseClasses.AppDatabase.DBPath, FileAccess.Read, FileShare.Read);
            NotifyController(MessageType.DATABASE_UPDATED);
            Q.Add(new TwRequest(TwMessageSimple.CreateRequest(MessageType.REINIT)));
        }

        private void NotifyController(MessageType msg)
        {
            VisibleState.ClientNotifs.Add(msg);
            GlobalInstances.ServerChangeset = Guid.NewGuid();
        }

        internal void TimerCallback(Object state)
        {
            Q.Add(new TwRequest(TwMessageSimple.CreateRequest(MessageType.MINUTE_TIMER)));
        }

        private List<FirewallLogEntry> GetFwLog()
        {
            var entries = new List<FirewallLogEntry>();
            lock (FirewallLogEntries)
            {
                entries.AddRange(FirewallLogEntries);
            }
            return entries;
        }

        private bool ApplyPromptAllow(BlockedConnectionPrompt prompt)
        {
            if (VisibleState.Mode != FirewallMode.Normal ||
                !PromptAllowPolicy.TryCreate(prompt.Identity, out PromptAllowPolicy? allowPolicy) ||
                allowPolicy == null)
            {
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
            try
            {
                ApplyConfiguration(candidate, VisibleState.Mode);
                return true;
            }
            catch (Exception exceptionToLog)
            {
                Utils.LogException(exceptionToLog, Utils.LOG_ID_SERVICE);
                return false;
            }
        }

        private void ApplyConfiguration(ServerConfiguration candidate, FirewallMode mode)
        {
            if (mode < FirewallMode.Normal || mode > FirewallMode.Learning ||
                candidate.StartupMode < FirewallMode.Normal || candidate.StartupMode > FirewallMode.AllowOutgoing)
                throw new ArgumentException("Unsupported runtime or startup firewall mode.");
            ServerConfiguration previous = ActiveConfig.Service;
            bool previousLearning = VisibleState.Mode == FirewallMode.Learning;
            ApplyingConfiguration = candidate;
            ApplyingMode = mode;
            try
            {
                PolicyChangeTransaction.Apply(
                    () => candidate.Save(ConfigSavePath),
                    () =>
                    {
                        LogWatcher.LearningEnabled = mode == FirewallMode.Learning;
                        ReapplySettings();
                        InstallFirewallRules();
                    },
                    () =>
                    {
                        previous.Save(ConfigSavePath);
                        ApplyingConfiguration = previous;
                        ApplyingMode = VisibleState.Mode;
                        LogWatcher.LearningEnabled = previousLearning;
                        ReapplySettings();
                    },
                    () =>
                    {
                        ActiveConfig.Service = candidate;
                        VisibleState.Mode = mode;
                        GlobalInstances.ServerChangeset = Guid.NewGuid();
                    },
                    FailClosed,
                    () => previous.Save(ConfigRecoveryPath),
                    () => File.Delete(ConfigRecoveryPath));
            }
            finally
            {
                ApplyingConfiguration = null;
                ApplyingMode = null;
            }
        }

        private void DisposeRuntimeSubscription()
        {
            // Keep the callback delegate rooted until the engine is closed, including
            // when SafeHandle.Dispose silently consumes a failed native unsubscribe.
            RuntimeEventSubscription?.Dispose();
        }

        private void FailClosed()
        {
            RuntimeStopping = true;
            RunService = false;
            if (!RuntimeSessionRevoked)
            {
                // Consume the subscription SafeHandle before invalidating the engine. Even
                // a failed native unsubscribe cannot delay the checked native session close.
                RuntimeSessionRevocation.Close(WfpEngine.NativePtr,
                    FwpmEngineSafeHandle.NativeMethods.FwpmEngineClose0,
                    error => Environment.FailFast("SecureWall cannot confirm withdrawal of runtime permissions.", error),
                    DisposeRuntimeSubscription);
                RuntimeSessionRevoked = true;
                RuntimeEventSubscription = null;
            }
            PromptableFilterIds.Replace(Array.Empty<ulong>());
            VisibleState.Mode = FirewallMode.Unknown;
            GlobalInstances.ServerChangeset = Guid.NewGuid();
            PasswordLock.Locked = true;
        }

        private void ExpireRules()
        {
            ExpiringPolicyMaintenance.Run(
                () => Utils.DeepClone(ActiveConfig.Service),
                candidate => PruneExpiredRules(candidate),
                candidate => ApplyConfiguration(candidate, VisibleState.Mode),
                FailClosed);
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

        private bool CommitLearnedRules()
        {
            bool config_changed = false;

            lock (LearningNewExceptions)
            {
                if (LearningNewExceptions.Count > 0)
                {
                    GlobalInstances.ServerChangeset = Guid.NewGuid();
                    ActiveConfig.Service.ActiveProfile.AddExceptions(LearningNewExceptions);
                    LearningNewExceptions.Clear();
                    config_changed = true;
                }
            }

            return config_changed;
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

        private TwMessage ProcessCmd(TwMessage req)
        {
            switch (req.Type)
            {
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
                        return args.CreateResponse(prompts);
                    }
                case MessageType.DISMISS_PROMPT:
                    {
                        var args = (TwMessagePromptAction)req;
                        PromptActionResult result = BlockedPromptQueue.Dismiss(args.Token);
                        return args.CreateResponse(result.Status);
                    }
                case MessageType.ALLOW_PROMPT:
                    {
                        var args = (TwMessagePromptAction)req;
                        PromptActionResult result = BlockedPromptQueue.Allow(args.Token, ApplyPromptAllow);
                        return args.CreateResponse(result.Status);
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

                        var candidate = Utils.DeepClone(ActiveConfig.Service);
                        lock (LearningNewExceptions)
                        {
                            candidate.ActiveProfile.AddExceptions(LearningNewExceptions.Select(item => Utils.DeepClone(item)).ToList());
                            if (newMode != FirewallMode.Disabled && newMode != FirewallMode.Learning)
                                candidate.StartupMode = newMode;
                            ApplyConfiguration(candidate, newMode);
                            LearningNewExceptions.Clear();
                        }
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
                                ApplyConfiguration(Utils.DeepClone(args.Config), VisibleState.Mode);
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
                        InstallFirewallRules();
                        return args.CreateResponse();
                    }
                case MessageType.UNLOCK:
                    {
                        var args = (TwMessageUnlock)req;
                        bool success = PasswordLock.Unlock(args.Password);
                        if (success)
                            return args.CreateResponse();
                        else
                            return TwMessageError.Instance;
                    }
                case MessageType.LOCK:
                    {
                        var args = (TwMessageSimple)req;
                        PasswordLock.Locked = true;
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
                            FileLocker.Lock(PasswordLock.PasswordFilePath, FileAccess.Read, FileShare.Read);
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

                        // Expiry precedes housekeeping which may itself fail. A failed save
                        // or replacement must never preserve an expired dynamic permission.
                        ExpireRules();

                        // Event collection might have been disabled by external process or user after we started up,
                        // so re-enable it if that is the case.
                        if (!WfpEngine.CollectNetEvents)
                            WfpEngine.CollectNetEvents = true;

                        // Check for inactivity and lock if necessary
                        if (ControllerActivity.Expired)
                        {
                            Q.Add(new TwRequest(TwMessageSimple.CreateRequest(MessageType.LOCK)));
                        }

                        // Periodically reload all rules.
                        // This is needed to clear out temprary rules added due to child-process rule inheritance.
                        if (DateTime.Now - LastRuleReloadTime > TimeSpan.FromMinutes(30))
                        {
                            rule_reload_needed = true;
                        }

                        if (rule_reload_needed)
                        {
                            InstallFirewallRules();
                        }

                        // Check for updates once every 2 days
                        if (ActiveConfig.Service.AutoUpdateCheck && (DateTime.Now - LastUpdateCheck >= TimeSpan.FromDays(2)))
                        {
                            LastUpdateCheck = DateTime.Now;
                            UpdaterMethod();
                        }

                        return args.CreateResponse();
                    }
                case MessageType.REENUMERATE_ADDRESSES:
                    {
                        var args = (TwMessageSimple)req;
                        if (ReenumerateAdresses())  // returns true if anything changed
                            InstallFirewallRules();
                        return args.CreateResponse();
                    }
                case MessageType.DISPLAY_POWER_EVENT:
                    {
                        var args = (TwMessageDisplayPowerEvent)req;
                        if (args.PowerOn != DisplayCurrentlyOn)
                        {
                            DisplayCurrentlyOn = args.PowerOn;
                            InstallFirewallRules();
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
            if (!NetworkAdapterEnumerator.EnumerateActiveAdapters(
                out var unicastList, out var newGatewayAddresses, out var newDnsAddresses))
            {
                return false;
            }

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

            bool ipConfigurationChanged =
                !LocalSubnetAddreses.SetEquals(newLocalSubnetAddreses) ||
                !GatewayAddresses.SetEquals(newGatewayAddresses) ||
                !DnsAddresses.SetEquals(newDnsAddresses);

            if (ipConfigurationChanged)
            {
                LocalSubnetAddreses = newLocalSubnetAddreses;
                GatewayAddresses = newGatewayAddresses;
                DnsAddresses = newDnsAddresses;

                LocalSubnetFilterConditions.Clear();
                GatewayFilterConditions.Clear();
                DnsFilterConditions.Clear();

                foreach (var addr in LocalSubnetAddreses)
                    LocalSubnetFilterConditions.Add(new IpFilterCondition(addr.Address, (byte)addr.PrefixLen, RemoteOrLocal.Remote));
                foreach (var addr in GatewayAddresses)
                    GatewayFilterConditions.Add(new IpFilterCondition(addr.Address, (byte)addr.PrefixLen, RemoteOrLocal.Remote));
                foreach (var addr in DnsAddresses)
                    DnsFilterConditions.Add(new IpFilterCondition(addr.Address, (byte)addr.PrefixLen, RemoteOrLocal.Remote));
            }

            return ipConfigurationChanged;
        }

        internal static void DeleteWfpObjects(Engine wfp, bool removeLayersAndProvider)
        {
            // WARNING! This method is super-slow if not executed inside a WFP transaction!
            using var timer = new HierarchicalStopwatch("DeleteWfpObjects()");
            var layerKeys = (LayerKeyEnum[])Enum.GetValues(typeof(LayerKeyEnum));
            foreach (var layer in layerKeys)
            {
                Guid layerKey = GetLayerKey(layer);
                Guid subLayerKey = GetSublayerKey(layer);

                // Remove filters in the sublayer
                foreach (var filterKey in wfp.EnumerateFilterKeys(SECUREWALL_PROVIDER_KEY, layerKey))
                    wfp.UnregisterFilter(filterKey);

                // Remove sublayer
                if (removeLayersAndProvider)
                    try { wfp.UnregisterSublayer(subLayerKey); } catch { }
            }

            // Remove provider
            if (removeLayersAndProvider)
                try { wfp.UnregisterProvider(SECUREWALL_PROVIDER_KEY); } catch { }
        }

        public TinyWallServer()
        {
            LogWatcher = new FirewallLogWatcher();
            Timer? minuteTimer = null;
            Timer? promptCandidateTimer = null;
            PipeServerEndpoint? serverPipe = null;
            try
            {
                // Fire up file protections as soon as possible
                FileLocker.Lock(DatabaseClasses.AppDatabase.DBPath, FileAccess.Read, FileShare.Read);
                FileLocker.Lock(PasswordLock.PasswordFilePath, FileAccess.Read, FileShare.Read);

                // Lock configuration if we have a password
                if (PasswordLock.HasPassword)
                    PasswordLock.Locked = true;

                LogWatcher.NewLogEntry += (sender, entry) => AutoLearnLogEntry(entry);
                LogWatcher.BlockedConnection += LogWatcherBlockedConnection;
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

            WfpEngine.CollectNetEvents = true;
            using var NetEventCollection = new CallbackOnDispose(() => { try { WfpEngine.CollectNetEvents = false; } catch { } });
            WfpEngine.EventMatchAnyKeywords = InboundEventMatchKeyword.FWPM_NET_EVENT_KEYWORD_INBOUND_BCAST | InboundEventMatchKeyword.FWPM_NET_EVENT_KEYWORD_INBOUND_MCAST;
            RuntimeEventSubscription = WfpEngine.SubscribeNetEvent(WfpNetEventCallback);
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
                WinDefFirewall?.Dispose();
            });
            InitFirewall();
            WinDefFirewall = new WindowsFirewall();
            service.FinishStateChange();
#if !DEBUG
            // Basic software health checks
            TinyWallDoctor.EnsureHealth(Utils.LOG_ID_SERVICE);
#endif

            MinuteTimer.Change(60000, 60000);
            RunService = true;
            while (RunService)
            {
                timer.NewSubTask("Message wait");
                var req = Q.Take();

                timer.NewSubTask($"Message {req.Request.Type}");
                try
                {
                    req.Response = ProcessCmd(req.Request);
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
                    // Skip if we have a user-defined rule for this path
                    if (UserSubjectExes.Contains(path))
                        return;

                    // This list will hold parents that we already checked for a process.
                    // Used to avoid infinite loop when parent-PID info is unreliable.
                    var pidsChecked = new HashSet<uint>();

                    // Start walking up the process tree
                    for (var parentPid = pid; ;)
                    {
                        if (!ProcessManager.GetParentProcess(parentPid, ref parentPid))
                            // We reached the top of the process tree (with non-existent parent)
                            break;

                        if (parentPid == 0)
                            // We reached top of process tree (with idle process)
                            break;

                        if (pidsChecked.Contains(parentPid))
                            // We've been here before, damn it. Avoid looping eternally...
                            break;

                        pidsChecked.Add(parentPid);

                        string parentPath = ProcessManager.GetProcessPath(parentPid, ref ProcessStartWatcher_Sbuilder);
                        if (string.IsNullOrEmpty(parentPath))
                            continue;

                        // Skip if we have already processed this parent-child combination
                        if (ChildInheritedSubjectExes.TryGetValue(path, out var childVar))
                        {
                            if (childVar.Contains(parentPath))
                                break;
                        }

                        if (ChildInheritance.TryGetValue(parentPath, out List<FirewallExceptionV3> exList))
                        {
                            newExceptions ??= new List<FirewallExceptionV3>();

                            foreach (var userEx in exList)
                                if (!EnforcementPolicy.IsExpired(userEx.CreationDate, (int)userEx.Timer, DateTime.Now))
                                    newExceptions.Add(new FirewallExceptionV3(new ExecutableSubject(path), userEx.Policy)
                                    {
                                        Timer = userEx.Timer,
                                        CreationDate = userEx.CreationDate
                                    });

                            if (!ChildInheritedSubjectExes.ContainsKey(path))
                                ChildInheritedSubjectExes.Add(path, new HashSet<string>());
                            ChildInheritedSubjectExes[path].Add(parentPath);
                            break;
                        }
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
                    entry.FilterRuntimeId = data.filterId.Value;

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
                    DropCandidates.TryAdd(candidate);
                }
            }
            catch (Exception exception)
            {
                Utils.LogException(exception, Utils.LOG_ID_SERVICE);
            }
        }

        private void LogWatcherBlockedConnection(
            FirewallLogWatcher sender,
            BlockedConnectionAuditEvent auditEvent)
        {
            if (!DropCandidates.TryMatch(auditEvent, out DropCandidate? candidate) || candidate == null)
                return;

            IEnumerable<string> serviceNames = Array.Empty<string>();
            try
            {
                var servicePids = new ServicePidMap();
                serviceNames = servicePids.GetServicesInPid(auditEvent.ProcessId);
            }
            catch (Exception exception)
            {
                Utils.LogException(exception, Utils.LOG_ID_SERVICE);
            }

            FinalizeDropCandidate(candidate, auditEvent, serviceNames);
        }

        private void PromptCandidateTimerCallback(object? state)
        {
            foreach (DropCandidate candidate in DropCandidates.DrainReady())
                FinalizeDropCandidate(candidate, null, Array.Empty<string>());
        }

        private void FinalizeDropCandidate(
            DropCandidate candidate,
            BlockedConnectionAuditEvent? auditEvent,
            IEnumerable<string> serviceNames)
        {
            if (VisibleState.Mode != FirewallMode.Normal)
                return;

            try
            {
                bool catalogAvailable = ServiceExecutables.TryContains(
                    candidate.ApplicationPath,
                    out bool executableIsRegisteredService);
                PromptIdentity identity = ServiceAttribution.Resolve(
                    candidate,
                    auditEvent,
                    serviceNames,
                    executableIsRegisteredService || !catalogAvailable);
                BlockedPromptQueue.Enqueue(
                    identity,
                    candidate.RemoteAddress,
                    candidate.RemotePort,
                    candidate.Protocol);
            }
            catch (Exception exception)
            {
                Utils.LogException(exception, Utils.LOG_ID_SERVICE);
            }
        }

        private void AutoLearnLogEntry(FirewallLogEntry entry)
        {
            if (  // IPv4
                ((string.Equals(entry.RemoteIp, "127.0.0.1", StringComparison.Ordinal)
                && string.Equals(entry.LocalIp, "127.0.0.1", StringComparison.Ordinal)))
               || // IPv6
                ((string.Equals(entry.RemoteIp, "::1", StringComparison.Ordinal)
                && string.Equals(entry.LocalIp, "::1", StringComparison.Ordinal)))
               )
            {
                // Ignore communication within local machine
                return;
            }

            // Certain things we don't want to whitelist
            if (Utils.IsNullOrEmpty(entry.AppPath)
                || string.Equals(entry.AppPath, "System", StringComparison.InvariantCultureIgnoreCase)
                || string.Equals(entry.AppPath, "svchost.exe", StringComparison.InvariantCultureIgnoreCase)
                )
                return;

            var newSubject = new ExecutableSubject(entry.AppPath);

            lock (LearningNewExceptions)
            {
                // A callback queued while a candidate enabled learning may arrive only
                // after that transition failed or completed. Trust committed mode here.
                if (RuntimeStopping || VisibleState.Mode != FirewallMode.Learning)
                    return;
                for (int j = 0; j < LearningNewExceptions.Count; ++j)
                {
                    if (LearningNewExceptions[j].Subject.Equals(newSubject))
                        // Already in LearningNewExceptions, nothing to do
                        return;
                }

                var exceptions = GlobalInstances.AppDatabase.GetExceptionsForApp(newSubject, false, out _);
                LearningNewExceptions.AddRange(exceptions);
            }
        }

        // Entry point for thread that listens to commands from the controller application.
        private TwMessage PipeServerDataReceived(TwMessage reqMsg)
        {
            if (((int)reqMsg.Type > 2047) && PasswordLock.Locked)
            {
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
                var req = new TwRequest(reqMsg);
                Q.Add(req);

                // Background reads never extend the password inactivity window.
                TwMessage response = req.Response;
                bool userAction = reqMsg.Type == MessageType.UNLOCK || reqMsg.Type == MessageType.DISMISS_PROMPT ||
                    reqMsg.Type == MessageType.ALLOW_PROMPT || reqMsg.Type == MessageType.MODE_SWITCH ||
                    reqMsg.Type == MessageType.PUT_SETTINGS || reqMsg.Type == MessageType.SET_PASSPHRASE;
                ControllerActivity.Record(userAction && response.Type != MessageType.RESPONSE_ERROR &&
                    response.Type != MessageType.RESPONSE_LOCKED && response.Type != MessageType.COM_ERROR);
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
            DisposeRuntimeSubscription();
            WfpEngine.Dispose();
            PromptableFilterIds.Replace(Array.Empty<ulong>());
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

            if (CommitLearnedRules())
                ActiveConfig.Service.Save(ConfigSavePath);

            RuleReloadEventMerger.Dispose();
            LocalSubnetFilterConditions.Dispose();
            GatewayFilterConditions.Dispose();
            DnsFilterConditions.Dispose();
            LogWatcher.BlockedConnection -= LogWatcherBlockedConnection;
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
            try
            {
                using (Server = new TinyWallServer())
                {
                    Server.Run(this);
                }
            }
            finally
            {
#if !DEBUG
                Thread.MemoryBarrier();
                if (!StopRequested && !IsComputerShuttingDown)    // Normal stop completion belongs to StopServer.
                {
                    SetServiceStateReached(ServiceState.Stopped);
                }
                if (!StopRequested)
                    Process.GetCurrentProcess().Kill();
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
