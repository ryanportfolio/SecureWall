using System;
using System.Diagnostics;
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
        private bool _allowInFlight;
        private readonly PromptAllowArming _arming = new PromptAllowArming();
        private readonly Stopwatch _clock = Stopwatch.StartNew();

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
            _allowInFlight = false;
            _arming.Reset();
            allowButton.Enabled = false;
            timeoutTimer.Interval = 250;
            SetAiPrompt(prompt);
            SetRiskPrompt(prompt);
            identityLabel.Text = IdentityText(prompt);
            pathLabel.Text = string.IsNullOrWhiteSpace(prompt.ExecutablePath)
                ? "No executable path was reported."
                : prompt.ExecutablePath;
            destinationLabel.Text = $"Destination: {ProtocolText(prompt.Protocol)}  {prompt.RemoteAddress}:{prompt.RemotePort}";
            statusLabel.Text = string.Empty;
            noticeLabel.Text = prompt.CanAllow
                ? "Allow permanently permits this app, package, or service to reach all destinations and ports over TCP/UDP."
                : "SecureWall could not identify one exact service. Allow is disabled to avoid broadly permitting a shared host.";
            toolTip.SetToolTip(
                allowButton,
                prompt.CanAllow
                    ? "Permanent outbound TCP/UDP access to all destinations and ports. The shown destination does not limit this rule."
                    : "Unavailable because the service identity is ambiguous.");

            PositionBottomRight();
            timeoutTimer.Start();
            Show();
            _arming.NoteShownOrMoved(_clock.Elapsed);
        }

        public void ShowActionFailure(PromptActionStatus status)
        {
            statusLabel.Text = status switch
            {
                PromptActionStatus.Locked => "SecureWall is locked. Enter the password to allow this app, or choose Ignore.",
                PromptActionStatus.NotAllowable => "This shared-service identity cannot be safely allowed.",
                PromptActionStatus.Expired => "This prompt expired; the connection remains blocked.",
                _ => "The request could not be completed. The connection remains blocked.",
            };
            if (status == PromptActionStatus.NotAllowable)
                _canAllow = false;
            if (status == PromptActionStatus.Locked)
                _deadline?.PauseForUnlock();
            _allowInFlight = false;
            UpdateAllowButton();
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
            // Any move or resize shifts what is under the cursor: disarm Allow again.
            _arming.NoteShownOrMoved(_clock.Elapsed);
            UpdateAllowButton();
        }

        private void UpdateAllowButton()
        {
            allowButton.Enabled = _canAllow && !_allowInFlight &&
                _arming.IsArmed(_clock.Elapsed) &&
                _deadline?.ShouldClose(DateTimeOffset.UtcNow) == false;
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

        private static string ProtocolText(byte protocol) => protocol switch
        {
            6 => "TCP",
            17 => "UDP",
            _ => $"Protocol {protocol}",
        };

        private void AllowButtonClick(object? sender, EventArgs eventArgs)
        {
            UpdateAllowButton();
            if (!allowButton.Enabled)
                return;
            _allowInFlight = true;
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
            if (_deadline?.ShouldClose(DateTimeOffset.UtcNow) != true)
            {
                UpdateAllowButton();
                return;
            }
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
