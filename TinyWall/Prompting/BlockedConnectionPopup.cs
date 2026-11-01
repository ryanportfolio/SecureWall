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

        // The popup never becomes the active window, so keystrokes meant for another app
        // (Enter, Space, Escape, Alt+F4) cannot answer or close it. Only mouse clicks can.
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int MA_NOACTIVATE = 3;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE)
            {
                m.Result = (IntPtr)MA_NOACTIVATE;
                return;
            }
            base.WndProc(ref m);
        }

        // A clicked control that takes focus calls SetFocus, which activates the popup
        // whenever another SecureWall window is in the foreground. The popup's controls
        // therefore never take focus.
        internal sealed class NoFocusButton : Button
        {
            internal NoFocusButton()
            {
                SetStyle(ControlStyles.Selectable, false);
            }
        }

        // The native edit control focuses itself on a click, so clicks inside the text are
        // dropped. The scroll bar and the mouse wheel still scroll it.
        internal sealed class NoFocusTextBox : TextBox
        {
            private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONDBLCLK = 0x0203,
                WM_RBUTTONDOWN = 0x0204, WM_RBUTTONDBLCLK = 0x0206,
                WM_MBUTTONDOWN = 0x0207, WM_MBUTTONDBLCLK = 0x0209;

            protected override void WndProc(ref Message m)
            {
                switch (m.Msg)
                {
                    case WM_LBUTTONDOWN:
                    case WM_LBUTTONDBLCLK:
                    case WM_RBUTTONDOWN:
                    case WM_RBUTTONDBLCLK:
                    case WM_MBUTTONDOWN:
                    case WM_MBUTTONDBLCLK:
                        return;
                }
                base.WndProc(ref m);
            }
        }

        private const string DefaultTitle = "SecureWall blocked an outgoing connection";

        public void ShowWaitingCount(int waiting)
        {
            // Called on every poll; only repaint on a change.
            string title = waiting > 0
                ? $"SecureWall blocked a connection (+{waiting} waiting)"
                : DefaultTitle;
            if (titleLabel.Text != title)
                titleLabel.Text = title;
        }

        public void ShowPrompt(PromptWireDto prompt)
        {
            if (prompt == null)
                throw new ArgumentNullException(nameof(prompt));

            _deadline = new PromptDisplayDeadline(prompt.ExpiresUtc);
            _canAllow = prompt.CanAllow;
            _allowInFlight = false;
            _arming.Reset();
            allowButton.Enabled = false;
            alwaysAllowButton.Enabled = false;
            timeoutTimer.Interval = 250;
            SetAiPrompt(prompt);
            SetRiskPrompt(prompt);
            SetAlwaysAllowPrompt(prompt);
            identityLabel.Text = IdentityText(prompt);
            pathLabel.Text = string.IsNullOrWhiteSpace(prompt.ExecutablePath)
                ? "No executable path was reported."
                : prompt.ExecutablePath;
            destinationLabel.Text = $"Destination: {ProtocolText(prompt.Protocol)}  {prompt.RemoteAddress}:{prompt.RemotePort}";
            statusLabel.Text = string.Empty;
            noticeLabel.Text = prompt.CanAllow
                ? "Allow permanently permits this app, package, or service to reach all destinations and ports over TCP/UDP."
                : PromptNoticeText.Blocked(prompt.AllowBlocker, prompt.ServiceName);
            toolTip.SetToolTip(
                allowButton,
                prompt.CanAllow
                    ? "Permanent outbound TCP/UDP access to all destinations and ports. The shown destination does not limit this rule."
                    : PromptNoticeText.BlockedTooltip(prompt.AllowBlocker));
            toolTip.SetToolTip(
                ignoreButton,
                prompt.SubjectKind == PromptIdentityKind.AmbiguousService
                    ? "Keep blocking. SecureWall cannot tell which service this is, so it asks again after five minutes."
                    : "Keep blocking and stop asking about this app. It is listed under Manage, Ignored; remove it there to be asked again. " +
                        "While SecureWall is locked, Ignore asks for the password first. If you cancel or the password is wrong, Ignore only stops asking for five minutes.");
            FitNoticeLabel();

            PositionBottomRight();
            timeoutTimer.Start();
            Show();
            _arming.NoteShownOrMoved(_clock.Elapsed);
        }

        public void ShowActionFailure(PromptActionStatus status)
        {
            statusLabel.Text = status switch
            {
                PromptActionStatus.Locked => "SecureWall is locked. Enter the password to allow this app, or close this prompt to keep it blocked.",
                PromptActionStatus.NotAllowable => "SecureWall cannot safely allow this identity.",
                PromptActionStatus.Expired => "This prompt expired; the connection remains blocked.",
                _ => "The request could not be completed. The connection remains blocked.",
            };
            if (status == PromptActionStatus.NotAllowable)
                _canAllow = false;
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
            alwaysAllowButton.Enabled = allowButton.Enabled && _alwaysAllowSigner != null;
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
            alwaysAllowButton.Enabled = false;
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
            alwaysAllowButton.Enabled = false;
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
