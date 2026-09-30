using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace pylorak.TinyWall.Prompting
{
    internal sealed partial class BlockedConnectionPopup : Form, IPromptView
    {
        private bool _closingProgrammatically;
        private PromptDisplayDeadline? _deadline;
        private bool _canAllow;

        internal BlockedConnectionPopup()
        {
            InitializeComponent();
            InitializeRiskWarnings();
            InitializeAiExplain();
        }

        public event EventHandler? AllowRequested;
        public event EventHandler? IgnoreRequested;
        public event EventHandler? PromptClosed;
        public event EventHandler? PromptTimedOut;

        protected override bool ShowWithoutActivation => true;

        public void ShowPrompt(PromptWireDto prompt)
        {
            if (prompt == null)
                throw new ArgumentNullException(nameof(prompt));

            _deadline = new PromptDisplayDeadline(DateTimeOffset.UtcNow, prompt.ExpiresUtc);
            _canAllow = prompt.CanAllow;
            timeoutTimer.Interval = 250;
            SetAiPrompt(prompt);
            SetRiskPrompt(prompt);
            identityLabel.Text = IdentityText(prompt);
            pathLabel.Text = string.IsNullOrWhiteSpace(prompt.ExecutablePath)
                ? "No executable path was reported."
                : prompt.ExecutablePath;
            destinationLabel.Text = $"Destination: {ProtocolText(prompt.Protocol)}  {prompt.RemoteAddress}:{prompt.RemotePort}";
            statusLabel.Text = string.Empty;
            allowButton.Enabled = prompt.CanAllow;
            noticeLabel.Text = prompt.CanAllow
                ? "Allow permanently permits this app, package, or service to reach all destinations and ports over TCP/UDP."
                : BlockedNotice(prompt);
            toolTip.SetToolTip(
                allowButton,
                prompt.CanAllow
                    ? "Permanent outbound TCP/UDP access to all destinations and ports. The shown destination does not limit this rule."
                    : BlockedTooltip(prompt));

            PositionBottomRight();
            timeoutTimer.Start();
            Show();
        }

        public void ShowActionFailure(PromptActionStatus status)
        {
            statusLabel.Text = status switch
            {
                PromptActionStatus.Locked => "SecureWall is locked. Unlock it from the tray, then try again.",
                PromptActionStatus.NotAllowable => "SecureWall cannot safely allow this identity.",
                PromptActionStatus.Expired => "This prompt expired; the connection remains blocked.",
                _ => "The request could not be completed. The connection remains blocked.",
            };
            allowButton.Enabled = _canAllow && status != PromptActionStatus.NotAllowable &&
                _deadline?.ShouldClose(DateTimeOffset.UtcNow) == false;
            timeoutTimer.Start();
        }

        public void ClosePrompt()
        {
            if (IsDisposed)
                return;

            _closingProgrammatically = true;
            timeoutTimer.Stop();
            Close();
        }

        private void PositionBottomRight()
        {
            Rectangle workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
            const int margin = 16;
            Location = new Point(
                workingArea.Right - Width - margin,
                workingArea.Bottom - Height - margin);
        }

        private static string IdentityText(PromptWireDto prompt)
        {
            return prompt.SubjectKind switch
            {
                PromptIdentityKind.Package => $"App package {prompt.PackageSid}",
                PromptIdentityKind.Service => $"{prompt.ServiceName} (Windows service)",
                PromptIdentityKind.AmbiguousService when prompt.AmbiguousServiceNames.Length > 0 =>
                    $"Shared service host: {string.Join(", ", prompt.AmbiguousServiceNames)}",
                PromptIdentityKind.AmbiguousService => "Shared Windows service host",
                _ => string.IsNullOrWhiteSpace(prompt.ExecutablePath)
                    ? "Unknown application"
                    : Path.GetFileName(prompt.ExecutablePath),
            };
        }

        private static string BlockedNotice(PromptWireDto prompt) => prompt.AllowBlocker switch
        {
            PromptAllowBlocker.ServiceSidUnavailable =>
                $"{prompt.ServiceName} has no service SID, so a rule for it cannot match. Admin fix: sc.exe sidtype {prompt.ServiceName} unrestricted, then restart it.",
            PromptAllowBlocker.ServiceSidUnverified =>
                $"SecureWall could not read the service SID type of {prompt.ServiceName}, so it cannot verify that a rule would match. Allow is disabled.",
            PromptAllowBlocker.ServiceRegistrationUnknown =>
                "SecureWall could not rule out that this program runs as a Windows service, so Allow is disabled. The service log has details.",
            _ => "SecureWall could not identify one exact service. Allow is disabled to avoid broadly permitting a shared host.",
        };

        private static string BlockedTooltip(PromptWireDto prompt) => prompt.AllowBlocker switch
        {
            PromptAllowBlocker.ServiceSidUnavailable => "Unavailable because the service has no service SID to match.",
            PromptAllowBlocker.ServiceSidUnverified => "Unavailable because the service SID type could not be read.",
            PromptAllowBlocker.ServiceRegistrationUnknown => "Unavailable because the service inventory could not confirm this program.",
            _ => "Unavailable because the service identity is ambiguous.",
        };

        private static string ProtocolText(byte protocol) => protocol switch
        {
            6 => "TCP",
            17 => "UDP",
            _ => $"Protocol {protocol}",
        };

        private void AllowButtonClick(object? sender, EventArgs eventArgs)
        {
            allowButton.Enabled = false;
            AllowRequested?.Invoke(this, EventArgs.Empty);
        }

        private void IgnoreButtonClick(object? sender, EventArgs eventArgs)
        {
            IgnoreRequested?.Invoke(this, EventArgs.Empty);
        }

        private void CloseButtonClick(object? sender, EventArgs eventArgs) => Close();

        private void TimeoutTimerTick(object? sender, EventArgs eventArgs)
        {
            if (_deadline?.ShouldClose(DateTimeOffset.UtcNow) != true) return;
            allowButton.Enabled = false;
            timeoutTimer.Stop();
            PromptTimedOut?.Invoke(this, EventArgs.Empty);
        }

        private void PopupFormClosing(object? sender, FormClosingEventArgs eventArgs)
        {
            if (_closingProgrammatically)
                return;

            eventArgs.Cancel = true;
            PromptClosed?.Invoke(this, EventArgs.Empty);
        }
    }
}
