using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.RegularExpressions;
using pylorak.TinyWall.Prompting;

namespace SecureWall.Core.Tests
{
    // AI help's network permit against the policy seen on an owner's machine, and what the
    // popup says when the request fails.
    internal static class AiHelpPermitTests
    {
        internal static IEnumerable<(string Name, Action Test)> Cases
        {
            get
            {
                yield return ("ai permit outranks an inherited block on securewall exe", PermitOutranksInheritedBlock);
                yield return ("both own-image permits use the own-image weight", OwnImagePermitsUseTheirWeight);
                yield return ("ai failure from a firewall drop names the switch to turn on", FirewallDropNamesTheSwitch);
                yield return ("ai failure names dns, tls and connection causes", OtherTransportCauses);
                yield return ("ai failure keeps its cause within the displayed error", FailureFitsTheErrorPanel);
                yield return ("own image warning says where the ai switch is", WarningNamesTheSwitch);
                yield return ("settings row sets the ai flag and never a special exception", SettingsRowSetsTheFlag);
            }
        }

        private static Dictionary<string, ulong> ServiceWeights()
        {
            string service = PromptTransactionIntegrationTests.Source("TinyWall/TinyWallService.cs");
            int start = service.IndexOf("private enum FilterWeights : ulong", StringComparison.Ordinal);
            AssertEx.True(start > 0, "FilterWeights not found.");
            string body = service.Substring(start, service.IndexOf('}', start) - start);
            var weights = new Dictionary<string, ulong>();
            foreach (Match m in Regex.Matches(body, @"(\w+) = ([\w.]+),"))
            {
                string value = m.Groups[2].Value;
                weights[m.Groups[1].Value] = value == "Prompting.AiExplainEgressPolicy.PermitWeight"
                    ? AiExplainEgressPolicy.PermitWeight
                    : ulong.Parse(value);
            }
            return weights;
        }

        private sealed record WfpFilter(string Name, ulong Weight, bool Permit, bool Matches);

        // Within one sublayer the matching filter with the highest weight decides.
        private static bool Permitted(IEnumerable<WfpFilter> filters) =>
            filters.Where(f => f.Matches).OrderByDescending(f => f.Weight).First().Permit;

        // The owner's policy on 2026-11-01: profile blocks with "child processes inherit" on
        // services.exe gave the SecureWall service, and so SecureWall.exe, an inherited block
        // at user-block weight. The AI permit was installed but sat below it.
        private static void PermitOutranksInheritedBlock()
        {
            Dictionary<string, ulong> w = ServiceWeights();
            WfpFilter[] Policy(ulong permitWeight, bool interactiveToken, bool blocklist = false) => new[]
            {
                new WfpFilter("Block everything", w["DefaultBlock"], false, true),
                new WfpFilter("inherited block on securewall.exe", w["UserBlock"], false, true),
                new WfpFilter("SecureWall AI assistant", permitWeight, true, interactiveToken),
                new WfpFilter("port blocklist", w["Blocklist"], false, blocklist),
            };

            AssertEx.False(Permitted(Policy(w["UserPermit"], true)), "Reproduction: at user-permit weight the inherited block wins.");
            AssertEx.True(Permitted(Policy(w["OwnImagePermit"], true)), "The controller's TCP 443 request must be permitted.");
            AssertEx.False(Permitted(Policy(w["OwnImagePermit"], false)), "The service token never matches the permit.");
            AssertEx.False(Permitted(Policy(w["OwnImagePermit"], true, blocklist: true)), "Blocklists still win.");

            AssertEx.True(w["OwnImagePermit"] > w["UserBlock"], "above user blocks");
            AssertEx.True(w["OwnImagePermit"] < w["RawSocketBlock"] && w["OwnImagePermit"] < w["Blocklist"], "below raw-socket rules and blocklists");
            AssertEx.Equal(w.Count, w.Values.Distinct().Count(), "Weights must be distinct.");
        }

        private static void OwnImagePermitsUseTheirWeight()
        {
            string service = PromptTransactionIntegrationTests.Source("TinyWall/TinyWallService.cs");
            foreach (string signature in new[]
            {
                "private void InstallAiAssistantPermit(string ntImagePath, LayerKeyEnum layer)",
                "private void InstallUpdatePermit(UpdateNetworkLease lease, string ntImagePath, LayerKeyEnum layer)",
            })
            {
                int start = service.IndexOf(signature, StringComparison.Ordinal);
                AssertEx.True(start > 0, signature);
                string permit = service.Substring(start, service.IndexOf("InstallWfpFilter(f, FilterGroup.Unknown);", start, StringComparison.Ordinal) - start);
                AssertEx.True(permit.Contains("(ulong)FilterWeights.OwnImagePermit"), signature);
            }
        }

        private static Exception FirewallDrop() =>
            new HttpRequestException("An error occurred while sending the request.",
                new WebException("Unable to connect to the remote server",
                    new SocketException((int)SocketError.AccessDenied), WebExceptionStatus.ConnectFailure, null));

        private static void FirewallDropNamesTheSwitch()
        {
            string off = AiExplainFailure.Describe(FirewallDrop(), permitOn: false, blockAllMode: false);
            AssertEx.True(off.StartsWith("SecureWall blocked the AI request because AI help's network access is off.", StringComparison.Ordinal), off);
            AssertEx.True(off.Contains("Special Exceptions") && off.Contains(AiExplainEgressPolicy.SpecialExceptionName), off);
            AssertEx.True(off.Contains("Details: An error occurred while sending the request. Unable to connect to the remote server."), off);
            AssertEx.True(off.Contains("(socket error 10013)"), off);

            string blockAll = AiExplainFailure.Describe(FirewallDrop(), permitOn: true, blockAllMode: true);
            AssertEx.True(blockAll.Contains("Block all mode") && blockAll.Contains("Normal mode"), blockAll);

            string on = AiExplainFailure.Describe(FirewallDrop(), permitOn: true, blockAllMode: false);
            AssertEx.False(on.Contains("is off"), on);
            AssertEx.True(on.Contains("port 443"), on);
            string unknown = AiExplainFailure.Describe(FirewallDrop(), permitOn: null, blockAllMode: false);
            AssertEx.False(unknown.Contains("is off"), "An unread flag is not reported as off.");
        }

        private static void OtherTransportCauses()
        {
            Exception Wrap(Exception inner, WebExceptionStatus status) =>
                new HttpRequestException("An error occurred while sending the request.", new WebException("x", inner, status, null));

            AssertEx.True(AiExplainFailure.Cause(Wrap(new SocketException((int)SocketError.HostNotFound), WebExceptionStatus.NameResolutionFailure), true, false)
                .Contains("host name could not be resolved"));
            AssertEx.True(AiExplainFailure.Cause(Wrap(new AuthenticationException("bad cert"), WebExceptionStatus.TrustFailure), true, false)
                .Contains("TLS"));
            AssertEx.True(AiExplainFailure.Cause(Wrap(new SocketException((int)SocketError.TimedOut), WebExceptionStatus.ConnectFailure), true, false)
                .Contains("could not be reached"));
            AssertEx.True(AiExplainFailure.Cause(Wrap(new Exception("y"), WebExceptionStatus.ProxyNameResolutionFailure), true, false)
                .Contains("proxy"));
            AssertEx.Equal("Could not reach the AI service.", AiExplainFailure.Cause(new HttpRequestException("z"), true, false));
        }

        private static void FailureFitsTheErrorPanel()
        {
            Exception deep = FirewallDrop();
            for (int i = 0; i < 5; ++i)
                deep = new HttpRequestException(new string('m', 200) + i, deep);
            foreach (bool? permitOn in new bool?[] { false, true, null })
            {
                string cause = AiExplainFailure.Cause(deep, permitOn, false);
                string shown = AiExplainText.FormatError(AiExplainFailure.Describe(deep, permitOn, false));
                AssertEx.True(shown.StartsWith(cause + "\r\n\r\nDetails: ", StringComparison.Ordinal), "The cause must never be cut: " + shown);
            }
            AssertEx.True(AiExplainResult.Unreachable(FirewallDrop()).TransportFailure != null);
        }

        private static void WarningNamesTheSwitch()
        {
            string warning = AiExplainEgressPolicy.OwnImageExceptionWarning;
            AssertEx.True(warning.Contains("Manage") && warning.Contains("Special Exceptions")
                && warning.Contains("\"" + AiExplainEgressPolicy.SpecialExceptionName + "\"") && warning.Contains("Optional"), warning);
            AssertEx.True(warning.Contains("LocalSystem"));
            AssertEx.True(AiExplainEgressPolicy.ServiceAccessOffMessage.Contains("Special Exceptions"));
        }

        private static void SettingsRowSetsTheFlag()
        {
            string row = PromptTransactionIntegrationTests.Source("TinyWall/SettingsForm.AiHelp.cs");
            AssertEx.True(row.Contains("AiExplainEgressPolicy.SpecialExceptionName"));
            AssertEx.True(row.Contains("TmpConfig.Service.AiAssistantEgress = state == CheckState.Checked;"));
            AssertEx.True(row.Contains("SetItemChecked(index, TmpConfig.Service.AiAssistantEgress)"));
            AssertEx.False(row.Contains("SpecialExceptions.Add"), "The row is not a database profile.");

            string form = PromptTransactionIntegrationTests.Source("TinyWall/SettingsForm.cs");
            int apply = form.IndexOf("if (TryApplyAiHelpRow(item, e.NewValue))", StringComparison.Ordinal);
            int add = form.IndexOf("TmpConfig.Service.ActiveProfile.SpecialExceptions.Add(item.Id);", StringComparison.Ordinal);
            AssertEx.True(apply > 0 && apply < add, "The row is handled before any SpecialExceptions change.");
            int fill = form.IndexOf("AddAiHelpRow();", StringComparison.Ordinal);
            AssertEx.True(fill > 0 && fill < form.IndexOf("LoadingSettings = false;", fill, StringComparison.Ordinal),
                "The row is added while loading, so filling it changes nothing.");
        }
    }
}
