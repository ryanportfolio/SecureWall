using System.Drawing;
using System.Windows.Forms;

namespace pylorak.TinyWall
{
    // "Always allow" tab: the file name + signer rules created from prompts or the tray list.
    // Removing a rule here stops new automatic allows; rules already added for exact paths
    // stay in the application list until removed there.
    internal partial class SettingsForm
    {
        private ListView listAlwaysAllow = null!;
        private Button btnAlwaysAllowRemove = null!;

        private void InitializeAlwaysAllowTab()
        {
            var page = new TabPage("Always allow") { Name = "tabAlwaysAllow", Padding = new Padding(6), UseVisualStyleBackColor = true };
            var help = new Label
            {
                Name = "lblAlwaysAllowHelp",
                Dock = DockStyle.Top,
                AutoSize = false,
                Text = "These apps are allowed outgoing connections from any folder, as long as the file has this name and a valid signature from this publisher. " +
                    "SecureWall adds an exact rule the first time each new copy is blocked; its first connection can fail once.",
            };
            listAlwaysAllow = new ListView
            {
                Name = "listAlwaysAllow",
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                ShowItemToolTips = true,
                TabIndex = 0,
            };
            listAlwaysAllow.Columns.Add("File name", 180);
            listAlwaysAllow.Columns.Add("Publisher", 320);
            listAlwaysAllow.SelectedIndexChanged += (_, _) => btnAlwaysAllowRemove.Enabled = listAlwaysAllow.SelectedItems.Count > 0;

            btnAlwaysAllowRemove = new Button
            {
                Name = "btnAlwaysAllowRemove",
                Text = "Remove",
                Image = GlobalInstances.RemoveBtnIcon,
                ImageAlign = ContentAlignment.MiddleLeft,
                TextImageRelation = TextImageRelation.ImageBeforeText,
                AutoSize = true,
                Enabled = false,
                TabIndex = 1,
            };
            btnAlwaysAllowRemove.Click += (_, _) => RemoveSelectedAlwaysAllowRules();
            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
            };
            buttons.Controls.Add(btnAlwaysAllowRemove);

            page.Controls.Add(listAlwaysAllow);
            page.Controls.Add(buttons);
            page.Controls.Add(help);
            tabControl1.TabPages.Add(page);
            // Added after the form scaled for DPI, so size by the inherited font: room for three lines.
            help.Height = help.Font.Height * 3 + 4;
        }

        private void FillAlwaysAllowList()
        {
            listAlwaysAllow.BeginUpdate();
            listAlwaysAllow.Items.Clear();
            foreach (AlwaysAllowRule rule in TmpConfig.Service.ActiveProfile.AlwaysAllowRules)
            {
                var item = new ListViewItem(rule.FileName) { Tag = rule, ToolTipText = rule.Signer };
                item.SubItems.Add(string.IsNullOrWhiteSpace(rule.SignerName) ? rule.Signer : rule.SignerName);
                listAlwaysAllow.Items.Add(item);
            }
            listAlwaysAllow.EndUpdate();
            btnAlwaysAllowRemove.Enabled = false;
        }

        private void RemoveSelectedAlwaysAllowRules()
        {
            foreach (ListViewItem item in listAlwaysAllow.SelectedItems)
                TmpConfig.Service.ActiveProfile.AlwaysAllowRules.Remove((AlwaysAllowRule)item.Tag);
            FillAlwaysAllowList();
        }
    }
}
