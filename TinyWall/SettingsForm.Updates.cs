using System;
using System.Drawing;
using System.Windows.Forms;

namespace pylorak.TinyWall
{
    // Maintenance tab: Check for updates, where TinyWall's update button used to be, and the
    // per-user "Include prereleases" choice.
    internal partial class SettingsForm
    {
        private Button btnCheckForUpdates = null!;
        private CheckBox chkIncludePrereleases = null!;

        // Raised with the checkbox's current state, which may not be saved yet.
        internal event EventHandler<bool>? CheckForUpdatesRequested;

        private void InitializeUpdateControls()
        {
            btnCheckForUpdates = new Button
            {
                Name = "btnCheckForUpdates",
                Text = "Check for updates",
                Location = btnWeb.Location,
                Size = btnWeb.Size,
                FlatStyle = btnWeb.FlatStyle,
                Image = GlobalInstances.WebBtnIcon,
                ImageAlign = ContentAlignment.MiddleLeft,
                TabIndex = btnWeb.TabIndex,
                UseVisualStyleBackColor = true,
            };
            btnCheckForUpdates.Click += (_, _) => CheckForUpdatesRequested?.Invoke(this, chkIncludePrereleases.Checked);
            chkIncludePrereleases = new CheckBox
            {
                Name = "chkIncludePrereleases",
                Text = "Include prereleases",
                AutoSize = true,
                Location = new Point(btnGithub.Left, btnGithub.Bottom + 6),
                TabIndex = btnGithub.TabIndex + 1,
                UseVisualStyleBackColor = true,
            };
            tabPage4.Controls.Add(btnCheckForUpdates);
            tabPage4.Controls.Add(chkIncludePrereleases);
        }

        private void LoadUpdateSettings()
        {
            chkIncludePrereleases.Checked = TmpConfig.Controller.IncludePrereleases;
        }

        // Stores the choice only when it differs from what the form showed, so an untouched
        // default stays unset.
        private void SaveUpdateSettings()
        {
            if (chkIncludePrereleases.Checked != TmpConfig.Controller.IncludePrereleases)
                TmpConfig.Controller.UpdateIncludePrereleases = chkIncludePrereleases.Checked;
        }
    }
}
