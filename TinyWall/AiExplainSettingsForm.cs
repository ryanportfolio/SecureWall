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
            ClientSize = new Size(460, 370);

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
                Text = "Default requests send the file name, unverified publisher, identity type, service name and package SID when present. "
                    + "The key is stored encrypted for your Windows account and never leaves this PC "
                    + "except in requests you trigger. Enabling also turns on a SecureWall permit for its own program: "
                    + "outbound TCP port 443 from signed-in accounts only, never the SecureWall service.",
                AutoSize = false,
                Dock = DockStyle.Fill,
                Height = 130,
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

            // The machine-wide permit follows the checkbox so it exists only while the
            // assistant is enabled. Failure leaves the controller settings saved.
            if (!TrySetServiceAccess(_enabled.Checked, out string? serviceError))
            {
                MessageBox.Show(this,
                    "Your AI assistant settings were saved, but SecureWall's network permit for the assistant could not be "
                        + (_enabled.Checked ? "turned on" : "turned off") + ".\n\n" + serviceError,
                    "AI Assistant", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            DialogResult = DialogResult.OK;
            Close();
        }

        // Sets ServerConfiguration.AiAssistantEgress through the authenticated pipe with the
        // same PUT_SETTINGS request the settings window uses, so the service password lock and
        // changeset check apply. The service decides what the flag installs.
        internal static bool TrySetServiceAccess(bool enabled, out string? error)
        {
            error = null;
            ServerConfiguration? current = ActiveConfig.Service;
            if (current != null && current.AiAssistantEgress == enabled)
                return true;

            Controller? pipe = GlobalInstances.Controller;
            if (pipe == null || current == null)
            {
                error = "SecureWall's service is not connected.";
                return false;
            }

            ServerConfiguration candidate = Utils.DeepClone(current);
            candidate.AiAssistantEgress = enabled;
            TwMessage response = pipe.SetServerConfig(candidate, GlobalInstances.ClientChangeset);
            switch (response.Type)
            {
                case MessageType.PUT_SETTINGS:
                    var args = (TwMessagePutSettings)response;
                    ActiveConfig.Service = args.Config;
                    GlobalInstances.ClientChangeset = args.Changeset;
                    if (args.Warning)
                    {
                        error = "SecureWall's settings changed in the meantime. Try again.";
                        return false;
                    }
                    if (ActiveConfig.Service.AiAssistantEgress != enabled)
                    {
                        error = "The SecureWall service did not accept the change.";
                        return false;
                    }
                    return true;
                case MessageType.RESPONSE_LOCKED:
                    error = "SecureWall is locked. Unlock it from the tray icon, then try again.";
                    return false;
                default:
                    error = "The SecureWall service could not apply the change.";
                    return false;
            }
        }
    }
}
