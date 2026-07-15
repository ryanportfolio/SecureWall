#nullable enable

using System.Drawing;
using System.Windows.Forms;

namespace pylorak.TinyWall.Prompting
{
    internal sealed partial class BlockedConnectionPopup
    {
        private System.ComponentModel.IContainer? components;
        private Panel titlePanel = null!;
        private Label titleLabel = null!;
        private Button closeButton = null!;
        private Label identityLabel = null!;
        private Label pathLabel = null!;
        private Label destinationLabel = null!;
        private Label noticeLabel = null!;
        private Label statusLabel = null!;
        private Button allowButton = null!;
        private Button ignoreButton = null!;
        private ToolTip toolTip = null!;
        private Timer timeoutTimer = null!;

        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            titlePanel = new Panel();
            titleLabel = new Label();
            closeButton = new Button();
            identityLabel = new Label();
            pathLabel = new Label();
            destinationLabel = new Label();
            noticeLabel = new Label();
            statusLabel = new Label();
            allowButton = new Button();
            ignoreButton = new Button();
            toolTip = new ToolTip(components);
            timeoutTimer = new Timer(components);
            titlePanel.SuspendLayout();
            SuspendLayout();

            titlePanel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            titlePanel.BackColor = Color.FromArgb(31, 41, 55);
            titlePanel.Controls.Add(titleLabel);
            titlePanel.Controls.Add(closeButton);
            titlePanel.Location = new Point(1, 1);
            titlePanel.Name = "titlePanel";
            titlePanel.Size = new Size(458, 42);

            titleLabel.AutoEllipsis = true;
            titleLabel.Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold, GraphicsUnit.Point);
            titleLabel.ForeColor = Color.White;
            titleLabel.Location = new Point(14, 10);
            titleLabel.Name = "titleLabel";
            titleLabel.Size = new Size(392, 23);
            titleLabel.Text = "SecureWall blocked an outgoing connection";

            closeButton.AccessibleName = "Ignore and close";
            closeButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            closeButton.BackColor = Color.FromArgb(31, 41, 55);
            closeButton.FlatAppearance.BorderSize = 0;
            closeButton.FlatStyle = FlatStyle.Flat;
            closeButton.Font = new Font("Segoe UI", 12F, FontStyle.Regular, GraphicsUnit.Point);
            closeButton.ForeColor = Color.White;
            closeButton.Location = new Point(414, 4);
            closeButton.Name = "closeButton";
            closeButton.Size = new Size(38, 34);
            closeButton.Text = "×";
            closeButton.UseVisualStyleBackColor = false;
            closeButton.Click += CloseButtonClick;

            identityLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            identityLabel.AutoEllipsis = true;
            identityLabel.Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold, GraphicsUnit.Point);
            identityLabel.Location = new Point(18, 57);
            identityLabel.Name = "identityLabel";
            identityLabel.Size = new Size(424, 25);

            pathLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            pathLabel.AutoEllipsis = true;
            pathLabel.Font = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point);
            pathLabel.ForeColor = Color.FromArgb(75, 85, 99);
            pathLabel.Location = new Point(18, 84);
            pathLabel.Name = "pathLabel";
            pathLabel.Size = new Size(424, 36);

            destinationLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            destinationLabel.AutoEllipsis = true;
            destinationLabel.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            destinationLabel.Location = new Point(18, 124);
            destinationLabel.Name = "destinationLabel";
            destinationLabel.Size = new Size(424, 22);

            noticeLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            noticeLabel.Font = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point);
            noticeLabel.ForeColor = Color.FromArgb(55, 65, 81);
            noticeLabel.Location = new Point(18, 150);
            noticeLabel.Name = "noticeLabel";
            noticeLabel.Size = new Size(424, 38);

            statusLabel.AccessibleName = "Prompt action status";
            statusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            statusLabel.AutoEllipsis = true;
            statusLabel.Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold, GraphicsUnit.Point);
            statusLabel.ForeColor = Color.FromArgb(185, 28, 28);
            statusLabel.Location = new Point(18, 190);
            statusLabel.Name = "statusLabel";
            statusLabel.Size = new Size(424, 22);

            allowButton.AccessibleName = "Allow outgoing connections";
            allowButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            allowButton.BackColor = Color.FromArgb(37, 99, 235);
            allowButton.FlatAppearance.BorderSize = 0;
            allowButton.FlatStyle = FlatStyle.Flat;
            allowButton.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold, GraphicsUnit.Point);
            allowButton.ForeColor = Color.White;
            allowButton.Location = new Point(226, 222);
            allowButton.Name = "allowButton";
            allowButton.Size = new Size(132, 36);
            allowButton.Text = "Allow outgoing";
            allowButton.UseVisualStyleBackColor = false;
            allowButton.Click += AllowButtonClick;

            ignoreButton.AccessibleName = "Ignore and keep blocking";
            ignoreButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            ignoreButton.BackColor = Color.FromArgb(229, 231, 235);
            ignoreButton.FlatAppearance.BorderSize = 0;
            ignoreButton.FlatStyle = FlatStyle.Flat;
            ignoreButton.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold, GraphicsUnit.Point);
            ignoreButton.ForeColor = Color.FromArgb(31, 41, 55);
            ignoreButton.Location = new Point(366, 222);
            ignoreButton.Name = "ignoreButton";
            ignoreButton.Size = new Size(76, 36);
            ignoreButton.Text = "Ignore";
            ignoreButton.UseVisualStyleBackColor = false;
            ignoreButton.Click += IgnoreButtonClick;

            timeoutTimer.Interval = 30000;
            timeoutTimer.Tick += TimeoutTimerTick;

            AcceptButton = allowButton;
            AccessibleDescription = "Shows an outgoing connection that SecureWall blocked.";
            AccessibleName = "SecureWall blocked connection";
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Color.White;
            CancelButton = ignoreButton;
            ClientSize = new Size(460, 270);
            Controls.Add(titlePanel);
            Controls.Add(identityLabel);
            Controls.Add(pathLabel);
            Controls.Add(destinationLabel);
            Controls.Add(noticeLabel);
            Controls.Add(statusLabel);
            Controls.Add(allowButton);
            Controls.Add(ignoreButton);
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = false;
            Name = "BlockedConnectionPopup";
            Padding = new Padding(1);
            ShowIcon = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            FormClosing += PopupFormClosing;
            titlePanel.ResumeLayout(false);
            ResumeLayout(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                components?.Dispose();
            base.Dispose(disposing);
        }
    }
}
