using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using pylorak.TinyWall.Prompting;

namespace pylorak.TinyWall
{
    // The Check for updates window: progress while checking or downloading, then the offered
    // release with its notes and the Install, Skip this version and Later buttons. Controls
    // are created in code. The release notes come from GitHub and are shown as plain text.
    internal sealed class UpdateDialog : Form
    {
        internal enum Choice { None, Install, Skip, Later }

        private readonly Label Heading;
        private readonly Label Details;
        private readonly Label NotesLabel;
        private readonly TextBox Notes;
        private readonly Label Notice;
        private readonly ProgressBar Progress;
        private readonly Button InstallButton;
        private readonly Button SkipButton;
        private readonly Button CloseButton;

        internal event EventHandler<Choice>? Chosen;

        internal UpdateDialog()
        {
            Text = "SecureWall update";
            Icon = Resources.Icons.firewall;
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = true;
            Font = SystemFonts.MessageBoxFont;
            // Scales the fixed label heights below with the font and display scaling.
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(600, 470);
            MinimumSize = new Size(480, 360);

            Heading = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 30, Font = new Font(Font.FontFamily, Font.Size * 1.3f, FontStyle.Bold) };
            Details = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 52 };
            NotesLabel = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 22,
                Text = "Release notes from GitHub:",
                Visible = false,
            };
            Notes = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                WordWrap = true,
                Visible = false,
                TabIndex = 3,
            };
            Notice = new Label
            {
                Dock = DockStyle.Bottom,
                AutoSize = false,
                Height = 64,
                Visible = false,
                Text = "Installing stops SecureWall and blocks all connections to other computers until the new version starts, " +
                    "usually under a minute. Open connections drop. Windows asks for administrator permission, then setup shows its own window.",
            };
            Progress = new ProgressBar { Dock = DockStyle.Bottom, Height = 18, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30 };

            InstallButton = new Button { Text = "&Install", AutoSize = true, Visible = false, TabIndex = 0 };
            SkipButton = new Button { Text = "&Skip this version", AutoSize = true, Visible = false, TabIndex = 1 };
            CloseButton = new Button { Text = "Cancel", AutoSize = true, TabIndex = 2 };
            InstallButton.Click += (_, _) => Choose(Choice.Install);
            SkipButton.Click += (_, _) => Choose(Choice.Skip);
            CloseButton.Click += (_, _) => Close();

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Padding = new Padding(0, 8, 0, 0),
            };
            buttons.Controls.Add(CloseButton);
            buttons.Controls.Add(SkipButton);
            buttons.Controls.Add(InstallButton);

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
            body.Controls.Add(Notes);
            body.Controls.Add(NotesLabel);
            body.Controls.Add(Details);
            body.Controls.Add(Heading);
            body.Controls.Add(Notice);
            body.Controls.Add(Progress);
            body.Controls.Add(buttons);
            Controls.Add(body);
            CancelButton = CloseButton;
            Utils.SetRightToLeft(this);
        }

        internal void ShowBusy(string heading, string details)
        {
            Heading.Text = heading;
            Details.Text = details;
            Progress.Style = ProgressBarStyle.Marquee;
            Progress.Visible = true;
            InstallButton.Visible = SkipButton.Visible = false;
            CloseButton.Text = "Cancel";
        }

        internal void ShowDownloadProgress(long received, long total)
        {
            if (total <= 0)
                return;
            Progress.Style = ProgressBarStyle.Continuous;
            Progress.Maximum = 1000;
            Progress.Value = (int)Math.Max(0, Math.Min(1000, received * 1000 / total));
        }

        internal void ShowAvailable(GitHubRelease release, ReleaseManifest manifest, Version installed)
        {
            Heading.Text = "SecureWall " + ReleaseVersion.Format(manifest.Version) + " is available";
            string published = release.PublishedAt?.ToLocalTime().ToString("d", CultureInfo.CurrentCulture) ?? "an unknown date";
            Details.Text = "Published " + published + (manifest.Prerelease ? " as a prerelease" : string.Empty) +
                ". You have " + ReleaseVersion.Format(ReleaseVersion.Normalize(installed)) +
                ". The installer is checked against the release's SHA256SUMS.txt after download.";
            Notes.Text = ReleaseCheck.PlainNotes(release.Notes);
            NotesLabel.Visible = Notes.Visible = Notice.Visible = true;
            Progress.Visible = false;
            InstallButton.Visible = SkipButton.Visible = true;
            CloseButton.Text = "&Later";
            AcceptButton = InstallButton;
            InstallButton.Focus();
        }

        internal void ShowNote(string note)
        {
            Details.Text = note;
        }

        internal void ShowMessage(string heading, string message)
        {
            Heading.Text = heading;
            Details.Text = string.Empty;
            Notes.Text = message;
            Notes.Visible = true;
            NotesLabel.Visible = Notice.Visible = false;
            Progress.Visible = false;
            InstallButton.Visible = SkipButton.Visible = false;
            CloseButton.Text = "Close";
            CloseButton.Focus();
        }

        private void Choose(Choice choice)
        {
            Chosen?.Invoke(this, choice);
        }
    }
}
