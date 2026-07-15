using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace pylorak.TinyWall.Prompting
{
    // Optional AI "what is this?" affordance on the block prompt. Controls are created in code
    // so the generated BlockedConnectionPopup.Designer.cs is left untouched. The lookup runs
    // in the controller process, is advisory only, and never changes the allow/block decision.
    internal sealed partial class BlockedConnectionPopup
    {
        private Button? _aiButton;
        private TextBox? _aiResult;
        private PromptWireDto? _aiCurrentPrompt;
        private CancellationTokenSource? _aiCts;
        private bool _aiPanelExpanded;
        private int _aiCollapsedHeight;

        private void InitializeAiExplain()
        {
            _aiButton = new Button
            {
                Text = "?",
                Size = new Size(26, 24),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                TabStop = false,
            };
            _aiButton.Location = new Point(ClientSize.Width - _aiButton.Width - 8, 8);
            _aiButton.Click += AiButtonClick;
            toolTip.SetToolTip(_aiButton, "Ask an AI assistant what this program likely is.");

            _aiResult = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Bottom,
                Height = 130,
                Visible = false,
                TabStop = false,
            };

            Controls.Add(_aiButton);
            Controls.Add(_aiResult);
            _aiButton.BringToFront();

            FormClosed += (_, _) =>
            {
                try { _aiCts?.Cancel(); } catch (ObjectDisposedException) { }
                _aiCts?.Dispose();
                _aiCts = null;
            };
        }

        // Called from ShowPrompt for each new prompt.
        private void SetAiPrompt(PromptWireDto prompt)
        {
            _aiCurrentPrompt = prompt;
            try { _aiCts?.Cancel(); } catch (ObjectDisposedException) { }
            _aiCts?.Dispose();
            _aiCts = null;

            if (_aiButton != null)
            {
                _aiButton.Enabled = true;
                _aiButton.Text = "?";
            }
            if (_aiResult != null)
            {
                _aiResult.Text = string.Empty;
                CollapseAiPanel();
            }
        }

        private async void AiButtonClick(object? sender, EventArgs eventArgs)
        {
            PromptWireDto? prompt = _aiCurrentPrompt;
            if (prompt == null || _aiButton == null)
                return;

            ControllerSettings cfg = ActiveConfig.Controller;
            if (!cfg.AiExplainEnabled
                || !AiExplainKeyProtection.TryUnprotect(cfg.AiExplainApiKeyProtected, out string apiKey))
            {
                if (OfferToConfigure())
                    return;
                cfg = ActiveConfig.Controller;
                if (!cfg.AiExplainEnabled
                    || !AiExplainKeyProtection.TryUnprotect(cfg.AiExplainApiKeyProtected, out apiKey))
                {
                    return;
                }
            }

            // Keep the prompt on screen while the user reads the answer.
            timeoutTimer.Stop();

            _aiButton.Enabled = false;
            ShowAiResult("Checking with the AI assistant…");

            _aiCts?.Dispose();
            _aiCts = new CancellationTokenSource();
            CancellationToken token = _aiCts.Token;

            string baseUrl = cfg.AiExplainBaseUrl;
            string model = cfg.AiExplainModel;
            bool includeRemote = cfg.AiExplainIncludeRemoteEndpoint;

            try
            {
                AiExplainResult result = await Task.Run(async () =>
                {
                    string? publisher = AuthenticodePublisher.TryGetPublisher(prompt.ExecutablePath);
                    AiExplainSubject subject = AiExplainSubject.FromPrompt(prompt, publisher, includeRemote);
                    var client = new OpenAiCompatibleExplainClient(baseUrl, model, apiKey);
                    return await client.ExplainAsync(subject, token).ConfigureAwait(false);
                }, token).ConfigureAwait(true);

                if (token.IsCancellationRequested || IsDisposed)
                    return;

                ShowAiResult(result.Success ? result.Text! : result.Error!);
            }
            catch (OperationCanceledException)
            {
                // Prompt was replaced or closed; nothing to show.
            }
            catch (Exception exception)
            {
                if (!IsDisposed)
                    ShowAiResult("The AI lookup failed: " + exception.Message);
            }
            finally
            {
                if (!IsDisposed && _aiButton != null)
                    _aiButton.Enabled = true;
            }
        }

        private bool OfferToConfigure()
        {
            DialogResult choice = MessageBox.Show(
                this,
                "The AI assistant is not set up yet. Configure it now?",
                "AI Assistant",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (choice != DialogResult.Yes)
                return true;

            using var form = new AiExplainSettingsForm();
            form.ShowDialog(this);
            return false;
        }

        private void ShowAiResult(string text)
        {
            if (_aiResult == null)
                return;
            _aiResult.Text = text;
            ExpandAiPanel();
        }

        private void ExpandAiPanel()
        {
            if (_aiResult == null || _aiPanelExpanded)
            {
                if (_aiResult != null)
                    _aiResult.Visible = true;
                return;
            }

            _aiCollapsedHeight = Height;
            _aiResult.Visible = true;
            Height += _aiResult.Height;
            _aiPanelExpanded = true;
            PositionBottomRight();
        }

        private void CollapseAiPanel()
        {
            if (_aiResult == null)
                return;
            _aiResult.Visible = false;
            if (_aiPanelExpanded && _aiCollapsedHeight > 0)
            {
                Height = _aiCollapsedHeight;
                _aiPanelExpanded = false;
                PositionBottomRight();
            }
        }
    }
}
