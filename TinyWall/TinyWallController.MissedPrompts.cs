using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using Microsoft.Samples.TaskDialog;
using pylorak.TinyWall.Prompting;

namespace pylorak.TinyWall
{
    // Tray list of prompts that left without an answer (expired, or revoked by a policy
    // change). The list is display data only: its tokens are dead. Allow from here goes
    // through the owner's normal exception path (password lock, exception editor when
    // enabled) and writes the same outbound-only rule as Allow in the popup.
    internal sealed partial class TinyWallController
    {
        private readonly ToolStripMenuItem mnuMissedPrompts = new() { Visible = false };

        private void InitMissedPromptsMenu(PromptDisplayCoordinator coordinator)
        {
            TrayMenu.Items.Insert(TrayMenu.Items.IndexOf(mnuConnections) + 1, mnuMissedPrompts);
            coordinator.MissedPromptsChanged += PromptCoordinator_MissedPromptsChanged;
            UpdateMissedPromptsMenu();
        }

        private void PromptCoordinator_MissedPromptsChanged(object? sender, MissedPromptsChangedEventArgs e)
        {
            UpdateMissedPromptsMenu();
            if (e.Added.Count == 1)
            {
                ShowBalloonTip(
                    $"SecureWall blocked {MissedPromptName(e.Added[0])} and the prompt was not answered. Click here to allow it, or find it later under \"{MissedPromptsMenuTitle}\" in the tray menu.",
                    ToolTipIcon.Warning, 10000, (_, args) => AllowMissedPrompt((PromptWireDto)args.Arg!, alwaysAllow: false), e.Added[0]);
            }
            else if (e.Added.Count > 1)
            {
                ShowBalloonTip(
                    $"SecureWall blocked {e.Added.Count} apps and their prompts were not answered. Find them under \"{MissedPromptsMenuTitle}\" in the tray menu.",
                    ToolTipIcon.Warning, 10000);
            }
        }

        private const string MissedPromptsMenuTitle = "Blocked apps not answered";

        private void UpdateMissedPromptsMenu()
        {
            IReadOnlyList<PromptWireDto> missed = PromptCoordinator?.MissedPrompts ?? Array.Empty<PromptWireDto>();
            mnuMissedPrompts.Text = $"{MissedPromptsMenuTitle} ({missed.Count})";
            mnuMissedPrompts.Visible = missed.Count > 0;

            // Disposing an item removes it from DropDownItems, so take a copy first.
            var oldItems = new ToolStripItem[mnuMissedPrompts.DropDownItems.Count];
            mnuMissedPrompts.DropDownItems.CopyTo(oldItems, 0);
            mnuMissedPrompts.DropDownItems.Clear();
            foreach (ToolStripItem old in oldItems)
                old.Dispose();
            foreach (PromptWireDto prompt in missed)
            {
                string destination = $"{prompt.RemoteAddress}:{prompt.RemotePort}";
                string when = prompt.LastSeenUtc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
                var item = new ToolStripMenuItem($"{MissedPromptName(prompt)}  to {destination}, {when}")
                {
                    ToolTipText = prompt.CanAllow
                        ? (prompt.ExecutablePath ?? prompt.PackageSid ?? string.Empty)
                        : PromptNoticeText.Blocked(prompt.AllowBlocker, prompt.ServiceName, prompt.ExecutablePath),
                    Tag = prompt,
                };
                var allow = new ToolStripMenuItem("Allow");
                allow.Click += (_, _) => AllowMissedPrompt(prompt, alwaysAllow: false);
                item.DropDownItems.Add(allow);
                if (prompt.SubjectKind == PromptIdentityKind.Executable && prompt.CanAllow &&
                    prompt.CoveredServiceNames.Length == 0)
                {
                    var always = new ToolStripMenuItem("Always allow (any folder, same publisher)");
                    always.Click += (_, _) => AllowMissedPrompt(prompt, alwaysAllow: true);
                    item.DropDownItems.Add(always);
                }
                mnuMissedPrompts.DropDownItems.Add(item);
            }
            if (missed.Count > 0)
            {
                mnuMissedPrompts.DropDownItems.Add(new ToolStripSeparator());
                var clear = new ToolStripMenuItem("Clear this list");
                clear.Click += (_, _) => PromptCoordinator?.ClearMissed();
                mnuMissedPrompts.DropDownItems.Add(clear);
            }
        }

        private static string MissedPromptName(PromptWireDto prompt)
        {
            return prompt.SubjectKind switch
            {
                PromptIdentityKind.Package => $"app package {prompt.PackageSid}",
                PromptIdentityKind.Service => $"{prompt.ServiceName} (Windows service)",
                PromptIdentityKind.AmbiguousService => "a shared Windows service host",
                _ => string.IsNullOrWhiteSpace(prompt.ExecutablePath)
                    ? "an unknown application"
                    : Path.GetFileName(prompt.ExecutablePath),
            };
        }

        // alwaysAllow also saves an Always allow rule for the file name and the publisher read
        // here from the file's verified signature, in the same password-gated save.
        private void AllowMissedPrompt(PromptWireDto prompt, bool alwaysAllow)
        {
            string name = MissedPromptName(prompt);
            if (!prompt.CanAllow)
            {
                Utils.ShowMessageBox(
                    $"SecureWall cannot allow {name} from here.\n{PromptNoticeText.Blocked(prompt.AllowBlocker, prompt.ServiceName, prompt.ExecutablePath)}",
                    Resources.Messages.TinyWall, TaskDialogCommonButtons.Ok, TaskDialogIcon.Information);
                return;
            }

            ExceptionSubject subject;
            switch (prompt.SubjectKind)
            {
                case PromptIdentityKind.Executable when !string.IsNullOrWhiteSpace(prompt.ExecutablePath):
                    subject = new ExecutableSubject(prompt.ExecutablePath!);
                    break;
                case PromptIdentityKind.Service when !string.IsNullOrWhiteSpace(prompt.ExecutablePath) && !string.IsNullOrWhiteSpace(prompt.ServiceName):
                    subject = new ServiceSubject(prompt.ExecutablePath!, prompt.ServiceName!);
                    break;
                case PromptIdentityKind.Package when !string.IsNullOrWhiteSpace(prompt.PackageSid):
                    subject = new AppContainerSubject(prompt.PackageSid!, prompt.PackageSid!, string.Empty, string.Empty);
                    break;
                default:
                    return;
            }

            string where = prompt.ExecutablePath ?? prompt.PackageSid ?? string.Empty;
            AlwaysAllowRule? rule = null;
            if (alwaysAllow && prompt.CoveredServiceNames.Length != 0)
                return;
            if (alwaysAllow)
            {
                SignedFileIdentity? signer = subject is ExecutableSubject ? SignedFileIdentity.TryRead(prompt.ExecutablePath) : null;
                if (signer == null)
                {
                    Utils.ShowMessageBox(
                        $"{name} has no valid embedded signature, so SecureWall could not recognize it in another folder.\nUse Allow for this copy instead.",
                        Resources.Messages.TinyWall, TaskDialogCommonButtons.Ok, TaskDialogIcon.Information);
                    return;
                }
                rule = new AlwaysAllowRule(name, signer.Subject, signer.Name);
            }
            string services = prompt.CoveredServiceNames.Length == 0
                ? string.Empty
                : "\n\n" + PromptNoticeText.CoveredServices(prompt.CoveredServiceNames);
            string question = rule == null
                ? $"Allow {name} to make outgoing connections?\n{where}\n\nThis creates a permanent rule for outgoing TCP and UDP connections to all destinations and ports, the same rule as Allow in the prompt. It was blocked while connecting to {prompt.RemoteAddress}:{prompt.RemotePort}.{services}"
                : $"Always allow {name} signed by {rule.SignerName}?\n{where}\n\nThis allows this copy now, and any later file named {name} with a valid signature from {rule.SignerName}, in any folder. Each gets the same outgoing-only TCP and UDP rule as Allow in the prompt. Remove the rule under Manage, Always allow.";
            if (Utils.ShowMessageBox(question, Resources.Messages.TinyWall,
                    TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No, TaskDialogIcon.Warning) != DialogResult.Yes)
                return;

            var policy = new TcpUdpPolicy
            {
                AllowedRemoteTcpConnectPorts = "*",
                AllowedRemoteUdpConnectPorts = "*",
            };
            // Like Allow in the popup, lock SecureWall again whenever this Allow asked for the
            // password, including after a cancel or a failed unlock.
            int passwordPrompts = PasswordPromptsShown;
            // AddExceptions reports failures itself. Keep the entry unless the service
            // confirmed the save: a cancelled editor or password dialog leaves the app blocked.
            bool saved = AddExceptions(new List<FirewallExceptionV3> { new FirewallExceptionV3(subject, policy) },
                alsoChange: rule == null ? null : config => AlwaysAllowRule.AddTo(config.ActiveProfile.AlwaysAllowRules, rule));
            if (PasswordPromptsShown != passwordPrompts)
                RelockAfterPrompt();
            if (saved)
                PromptCoordinator?.RemoveMissed(prompt);
        }
    }
}
