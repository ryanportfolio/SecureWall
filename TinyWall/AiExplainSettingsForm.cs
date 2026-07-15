using System;
using System.Drawing;
using System.Windows.Forms;
using pylorak.TinyWall.Prompting;

namespace pylorak.TinyWall
{
    // Small self-contained configuration dialog for the optional AI assistant. Controls are
    // created in code (no .Designer.cs) so it is fully self-contained. It reads and writes
    // ActiveConfig.Controller and persists via ControllerSettings.Save(). The API key is
    // DPAPI-protected before it is stored.
    internal sealed class AiExplainSettingsForm : Form
    {
        private readonly CheckBox _enabled;
        private readonly TextBox _apiKey;
        private readonly TextBox _model;
        private readonly TextBox _baseUrl;
        private readonly CheckBox _includeRemote;

        internal AiExplainSettingsForm()
        {
            Text = "AI Assistant";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(460, 300);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Padding = new Padding(12),
                AutoSize = true,
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _enabled = new CheckBox { Text = "Enable AI \"what is this?\" help", AutoSize = true };
            _apiKey = new TextBox { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
            _model = new TextBox { Dock = DockStyle.Fill };
            _baseUrl = new TextBox { Dock = DockStyle.Fill };
            _includeRemote = new CheckBox
            {
                Text = "Also send the attempted destination (less private)",
                AutoSize = true,
            };

            AddRow(layout, string.Empty, _enabled);
            AddRow(layout, "API key:", _apiKey);
            AddRow(layout, "Model:", _model);
            AddRow(layout, "Base URL:", _baseUrl);
            AddRow(layout, string.Empty, _includeRemote);

            var note = new Label
            {
                Text = "Only the program's file name and code-signing publisher are sent by default. "
                    + "The key is stored encrypted for your Windows account and never leaves this PC "
                    + "except in requests you trigger.",
                AutoSize = false,
                Dock = DockStyle.Fill,
                Height = 60,
            };
            layout.Controls.Add(note);
            layout.SetColumnSpan(note, 2);

            var save = new Button { Text = "Save", DialogResult = DialogResult.None, AutoSize = true };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            save.Click += SaveClick;

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(12),
                AutoSize = true,
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(save);

            Controls.Add(layout);
            Controls.Add(buttons);
            AcceptButton = save;
            CancelButton = cancel;

            LoadFromConfig();
        }

        private static void AddRow(TableLayoutPanel layout, string label, Control control)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left });
            layout.Controls.Add(control);
        }

        private void LoadFromConfig()
        {
            ControllerSettings cfg = ActiveConfig.Controller;
            _enabled.Checked = cfg.AiExplainEnabled;
            _model.Text = string.IsNullOrWhiteSpace(cfg.AiExplainModel)
                ? AiExplainSettings.DefaultModel
                : cfg.AiExplainModel;
            _baseUrl.Text = string.IsNullOrWhiteSpace(cfg.AiExplainBaseUrl)
                ? AiExplainSettings.DefaultBaseUrl
                : cfg.AiExplainBaseUrl;
            _includeRemote.Checked = cfg.AiExplainIncludeRemoteEndpoint;
            // Show a placeholder if a key is already stored; leave blank means "keep existing".
            _apiKey.Text = AiExplainKeyProtection.TryUnprotect(cfg.AiExplainApiKeyProtected, out _)
                ? "********"
                : string.Empty;
        }

        private void SaveClick(object? sender, EventArgs e)
        {
            string baseUrl = _baseUrl.Text.Trim();
            string model = _model.Text.Trim();

            if (_enabled.Checked && !AiExplainSettings.Validate(baseUrl, model, out string? error))
            {
                MessageBox.Show(this, error, "AI Assistant", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            ControllerSettings cfg = ActiveConfig.Controller;
            cfg.AiExplainEnabled = _enabled.Checked;
            cfg.AiExplainBaseUrl = baseUrl;
            cfg.AiExplainModel = model;
            cfg.AiExplainIncludeRemoteEndpoint = _includeRemote.Checked;

            // Only replace the stored key when the user typed a new one (not the placeholder).
            string typed = _apiKey.Text;
            if (typed != "********")
                cfg.AiExplainApiKeyProtected = AiExplainKeyProtection.Protect(typed.Trim());

            cfg.Save();
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
