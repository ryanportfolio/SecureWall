using System;
using System.IO;
using System.Diagnostics.Eventing.Reader;
using NetFwTypeLib;
using System.Collections.Generic;
using Microsoft.Win32;
using pylorak.TinyWall.Prompting;
using pylorak.Utilities;
using System.ServiceProcess;

namespace pylorak.TinyWall
{
    class WindowsFirewall : Disposable
    {
        private readonly EventLogWatcher? WFEventWatcher;
        private static readonly object Sync = new object();
        private static bool Active;
        private const string RecoveryKey = @"SOFTWARE\SecureWall\FirewallRecovery";
        private const string NotificationValue = "OriginalNotifications";
        private static readonly NET_FW_PROFILE_TYPE2_[] Profiles = {
            NET_FW_PROFILE_TYPE2_.NET_FW_PROFILE2_DOMAIN,
            NET_FW_PROFILE_TYPE2_.NET_FW_PROFILE2_PRIVATE,
            NET_FW_PROFILE_TYPE2_.NET_FW_PROFILE2_PUBLIC
        };

        // This is a list of apps that are allowed to change firewall rules
        private static readonly string[] WhitelistedApps = new string[]
        {
#if DEBUG
            Path.Combine(Path.GetDirectoryName(Utils.ExecutablePath), "SecureWall.vshost.exe"),
#endif
            Utils.ExecutablePath,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dllhost.exe")
        };

        public WindowsFirewall()
        {
            lock (Sync) Active = true;
            DisableMpsSvc();

            try
            {
                WFEventWatcher = new EventLogWatcher("Microsoft-Windows-Windows Firewall With Advanced Security/Firewall");
                WFEventWatcher.EventRecordWritten += new EventHandler<EventRecordWrittenEventArgs>(WFEventWatcher_EventRecordWritten);
                WFEventWatcher.Enabled = true;
            }
            catch(Exception e)
            {
                Utils.Log("Cannot monitor Windows Firewall. Is the 'eventlog' service running? For details see next log entry.", Utils.LOG_ID_SERVICE);
                Utils.LogException(e, Utils.LOG_ID_SERVICE);
            }
        }

        private static void WFEventWatcher_EventRecordWritten(object? sender, EventRecordWrittenEventArgs e)
        {
            try
            {
                int propidx = -1;
                switch (e.EventRecord.Id)
                {
                    case 2003:     // firewall setting changed
                        {
                            propidx = 7;
                            break;
                        }
                    case 2005:     // rule changed
                        {
                            propidx = 22;
                            break;
                        }
                    case 2006:     // rule deleted
                        {
                            propidx = 3;
                            break;
                        }
                    case 2032:     // firewall has been reset
                        {
                            propidx = 1;
                            break;
                        }
                    default:
                        // Nothing to do
                        return;
                }

                System.Diagnostics.Debug.Assert(propidx != -1);

                // If the rules were changed by us, do nothing
                string EVpath = (string)e.EventRecord.Properties[propidx].Value;
                for (int i = 0; i < WhitelistedApps.Length; ++i)
                {
                    if (string.Compare(WhitelistedApps[i], EVpath, StringComparison.OrdinalIgnoreCase) == 0)
                        return;
                }
            }
            catch { }
            finally
            {
                e.EventRecord?.Dispose();
            }

            try { DisableMpsSvc(); }
            catch (Exception exception) { Utils.LogException(exception, Utils.LOG_ID_SERVICE); }
        }

        protected override void Dispose(bool disposing)
        {
            if (IsDisposed)
                return;

            if (disposing)
            {
                WFEventWatcher?.Dispose();
            }

            lock (Sync)
            {
                Active = false;
                try { RestoreOwnedState(); }
                catch (Exception exception) { Utils.LogException(exception, Utils.LOG_ID_SERVICE); }
            }
            base.Dispose(disposing);
        }

        private static INetFwPolicy2 GetFwPolicy2()
        {
            RequireServiceRunning();
            Type tNetFwPolicy2 = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            return (INetFwPolicy2)Activator.CreateInstance(tNetFwPolicy2);
        }

        private static INetFwRule CreateFwRule(string name, NET_FW_ACTION_ action, NET_FW_RULE_DIRECTION_ dir)
        {
            Type tNetFwRule = Type.GetTypeFromProgID("HNetCfg.FwRule");
            INetFwRule rule = (INetFwRule)Activator.CreateInstance(tNetFwRule);

            rule.Name = name;
            rule.Action = action;
            rule.Direction = dir;
            rule.Grouping = FirewallRecoveryPolicy.Group;
            rule.Profiles = (int)NET_FW_PROFILE_TYPE2_.NET_FW_PROFILE2_PRIVATE | (int)NET_FW_PROFILE_TYPE2_.NET_FW_PROFILE2_PUBLIC | (int)NET_FW_PROFILE_TYPE2_.NET_FW_PROFILE2_DOMAIN;
            rule.Enabled = true;
            if ((NET_FW_RULE_DIRECTION_.NET_FW_RULE_DIR_IN == dir) && (NET_FW_ACTION_.NET_FW_ACTION_ALLOW == action))
                rule.EdgeTraversal = true;

            return rule;
        }

        private static RegistryKey OpenRecoveryKey()
        {
            // All installer/service architectures share one durable journal.
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            return machine.CreateSubKey(RecoveryKey, true);
        }

        internal static void RequireServiceRunning()
        {
            using var service = new ServiceController("MpsSvc");
            if (service.Status != ServiceControllerStatus.Running)
                throw new InvalidOperationException("Windows Defender Firewall (MpsSvc) must be running before SecureWall can start or restore its compatibility settings. Start that service and retry.");
        }

        private static void DisableMpsSvc()
        {
            lock (Sync)
            {
                if (!Active) return;
                INetFwPolicy2 policy = GetFwPolicy2();
                // Reject foreign reserved names even when no owned rule exists,
                // before writing the journal, notifications, or rules.
                FirewallRecoveryPolicy.AcquireForRules(ReadRuleIdentities(policy), () => {
                    using var journal = OpenRecoveryKey();
                    FirewallRecoveryPolicy.Acquire(
                        () => (int?)journal.GetValue(NotificationValue),
                        () => new[] { policy.NotificationsDisabled[Profiles[0]], policy.NotificationsDisabled[Profiles[1]], policy.NotificationsDisabled[Profiles[2]] },
                        original => { journal.SetValue(NotificationValue, original, RegistryValueKind.DWord); journal.Flush(); },
                        () => {
                            RemoveOwnedRules(policy);
                            foreach (var profile in Profiles) policy.NotificationsDisabled[profile] = true;
                            policy.Rules.Add(CreateFwRule(FirewallRecoveryPolicy.Inbound, NET_FW_ACTION_.NET_FW_ACTION_ALLOW, NET_FW_RULE_DIRECTION_.NET_FW_RULE_DIR_IN));
                            policy.Rules.Add(CreateFwRule(FirewallRecoveryPolicy.Outbound, NET_FW_ACTION_.NET_FW_ACTION_ALLOW, NET_FW_RULE_DIRECTION_.NET_FW_RULE_DIR_OUT));
                        });
                });
            }
        }

        private static IEnumerable<KeyValuePair<string, string>> ReadRuleIdentities(INetFwPolicy2 policy)
        {
            foreach (INetFwRule rule in policy.Rules)
                yield return new KeyValuePair<string, string>(rule.Name, rule.Grouping);
        }

        private static void RemoveOwnedRules(INetFwPolicy2 policy)
        {
            foreach (string name in FirewallRecoveryPolicy.OwnedRuleNames(ReadRuleIdentities(policy)))
                policy.Rules.Remove(name);
            foreach (INetFwRule rule in policy.Rules)
                if (FirewallRecoveryPolicy.OwnsRule(rule.Name, rule.Grouping))
                    throw new InvalidOperationException("Owned compatibility rule could not be removed.");
        }

        // Independent crash recovery: cleanup must succeed before uninstall deletes
        // persistent WFP protection. Preserve the journal if any restoration fails.
        internal static void RestoreOwnedState()
        {
            lock (Sync)
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var existingJournal = machine.OpenSubKey(RecoveryKey);
                using var service = new ServiceController("MpsSvc");
                // This ownership generation flushes the journal before adding any
                // compatibility rule and clears it only after rule removal. A
                // stopped MpsSvc with no record therefore needs no COM recovery.
                // Existing, invalid, or unreadable records must still fail closed.
                if (FirewallRecoveryPolicy.CanSkipStoppedServiceRecovery(
                    service.Status == ServiceControllerStatus.Stopped,
                    existingJournal?.GetValue(NotificationValue) != null)) return;
                INetFwPolicy2 policy = GetFwPolicy2();
                using var journal = OpenRecoveryKey();
                FirewallRecoveryPolicy.Restore(
                    () => RemoveOwnedRules(policy),
                    () => (int?)journal.GetValue(NotificationValue),
                    original => { for (int i = 0; i < Profiles.Length; i++) policy.NotificationsDisabled[Profiles[i]] = original[i]; },
                    () => { journal.DeleteValue(NotificationValue); journal.Flush(); });
            }
        }
    }
}
