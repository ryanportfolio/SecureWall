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
                    ToolTipIcon.Warning, 10000, (_, args) => AllowMissedPrompt((PromptWireDto)args.Arg!), e.Added[0]);
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

            foreach (ToolStripItem old in mnuMissedPrompts.DropDownItems)
                old.Dispose();
            mnuMissedPrompts.DropDownItems.Clear();
            foreach (PromptWireDto prompt in missed)
            {
                string destination = $"{prompt.RemoteAddress}:{prompt.RemotePort}";
                string when = prompt.LastSeenUtc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
                var item = new ToolStripMenuItem($"{MissedPromptName(prompt)}  to {destination}, {when}")
                {
                    ToolTipText = prompt.CanAllow
                        ? (prompt.ExecutablePath ?? prompt.PackageSid ?? string.Empty)
                        : PromptNoticeText.Blocked(prompt.AllowBlocker, prompt.ServiceName),
                    Tag = prompt,
                };
                item.Click += (sender, _) => AllowMissedPrompt((PromptWireDto)((ToolStripItem)sender).Tag);
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

        private void AllowMissedPrompt(PromptWireDto prompt)
        {
            string name = MissedPromptName(prompt);
            if (!prompt.CanAllow)
            {
                Utils.ShowMessageBox(
                    $"SecureWall cannot allow {name} from here.\n{PromptNoticeText.Blocked(prompt.AllowBlocker, prompt.ServiceName)}",
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
            if (Utils.ShowMessageBox(
                    $"Allow {name} to make outgoing connections?\n{where}\n\nThis creates a permanent rule for outgoing TCP and UDP connections to all destinations and ports, the same rule as Allow in the prompt. It was blocked while connecting to {prompt.RemoteAddress}:{prompt.RemotePort}.",
                    Resources.Messages.TinyWall, TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No, TaskDialogIcon.Warning) != DialogResult.Yes)
                return;

            var policy = new TcpUdpPolicy
            {
                AllowedRemoteTcpConnectPorts = "*",
                AllowedRemoteUdpConnectPorts = "*",
            };
            AddExceptions(new List<FirewallExceptionV3> { new FirewallExceptionV3(subject, policy) });

            // AddExceptions reports its own result; drop the entry only once the rule is saved.
            foreach (FirewallExceptionV3 ex in ActiveConfig.Service.ActiveProfile.AppExceptions)
            {
                if (ex.Subject.Equals(subject))
                {
                    PromptCoordinator?.RemoveMissed(prompt);
                    break;
                }
            }
        }
    }
}
