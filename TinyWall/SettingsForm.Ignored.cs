using System.Drawing;
using System.Windows.Forms;

namespace pylorak.TinyWall
{
    // "Ignored" tab: apps the owner chose Ignore for in the popup. They stay blocked and are
    // not prompted; removing one makes it prompt again.
    internal partial class SettingsForm
    {
        private ListView listIgnored = null!;
        private Button btnIgnoredRemove = null!;

        private void InitializeIgnoredTab()
        {
            var page = new TabPage("Ignored") { Name = "tabIgnored", Padding = new Padding(6), UseVisualStyleBackColor = true };
            var help = new Label
            {
                Name = "lblIgnoredHelp",
                Dock = DockStyle.Top,
                AutoSize = false,
                Text = "You chose Ignore for these apps. Their blocked connections stay blocked, and SecureWall no longer asks about them. " +
                    "Remove an app to be asked again the next time it is blocked.",
            };
            listIgnored = new ListView
            {
                Name = "listIgnored",
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                ShowItemToolTips = true,
                TabIndex = 0,
            };
            listIgnored.Columns.Add("Name", 180);
            listIgnored.Columns.Add("Path or package", 320);
            listIgnored.SelectedIndexChanged += (_, _) => btnIgnoredRemove.Enabled = listIgnored.SelectedItems.Count > 0;

            btnIgnoredRemove = new Button
            {
                Name = "btnIgnoredRemove",
                Text = "Remove",
                Image = GlobalInstances.RemoveBtnIcon,
                ImageAlign = ContentAlignment.MiddleLeft,
                TextImageRelation = TextImageRelation.ImageBeforeText,
                AutoSize = true,
                Enabled = false,
                TabIndex = 1,
            };
            btnIgnoredRemove.Click += (_, _) => RemoveSelectedIgnoredApps();
            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
            };
            buttons.Controls.Add(btnIgnoredRemove);

            page.Controls.Add(listIgnored);
            page.Controls.Add(buttons);
            page.Controls.Add(help);
            tabControl1.TabPages.Add(page);
            // Added after the form scaled for DPI, so size by the inherited font: room for three lines.
            help.Height = help.Font.Height * 3 + 4;
        }

        private void FillIgnoredList()
        {
            listIgnored.BeginUpdate();
            listIgnored.Items.Clear();
            foreach (IgnoredApp app in TmpConfig.Service.ActiveProfile.IgnoredApps)
            {
                var item = new ListViewItem(app.DisplayName) { Tag = app, ToolTipText = app.Detail };
                item.SubItems.Add(app.Detail);
                listIgnored.Items.Add(item);
            }
            listIgnored.EndUpdate();
            btnIgnoredRemove.Enabled = false;
        }

        private void RemoveSelectedIgnoredApps()
        {
            foreach (ListViewItem item in listIgnored.SelectedItems)
                TmpConfig.Service.ActiveProfile.IgnoredApps.Remove((IgnoredApp)item.Tag);
            FillIgnoredList();
        }
    }
}
