using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace pylorak.TinyWall.Prompting
{
    // Warning lines about the blocked executable (unsigned, user-writable folder, changed
    // recently). The facts are gathered off the UI thread in the controller process when
    // the prompt is shown; the label appears between the notice and the status line and
    // grows the popup by its own height. Advisory only: it never changes what Allow does.
    internal sealed partial class BlockedConnectionPopup
    {
        private const int RiskLineHeight = 18;
        private const int RiskLabelGap = 4;

        private Label? _riskLabel;
        private int _riskHeight;
        private Guid _riskToken;

        // Replaceable so the /promptpreview path can show all three warnings without a
        // matching file on disk. Defaults to the real probe.
        internal Func<string?, ExecutableRiskFlags?> RiskProbe { get; set; } = ExecutableRiskProbe.Assess;

        private void InitializeRiskWarnings()
        {
            _riskLabel = new Label
            {
                AccessibleName = "Executable warnings",
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                AutoSize = false,
                Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(180, 83, 9),
                Location = new Point(18, statusLabel.Top),
                Size = new Size(424, 0),
                TabStop = false,
                Visible = false,
            };
            Controls.Add(_riskLabel);
        }

        // Called from ShowPrompt for each new prompt: clears the previous warnings and
        // starts a fresh probe whose result is dropped if the prompt changes meanwhile.
        private void SetRiskPrompt(PromptWireDto prompt)
        {
            ApplyRiskFlags(ExecutableRiskFlags.None);
            _riskToken = prompt.Token;

            string? path = prompt.ExecutablePath;
            Func<string?, ExecutableRiskFlags?> probe = RiskProbe;
            Guid token = prompt.Token;
            Task.Run(() =>
            {
                try
                {
                    return probe(path);
                }
                catch (Exception)
                {
                    return null;
                }
            }).ContinueWith(task =>
            {
                if (IsDisposed || !IsHandleCreated || token != _riskToken)
                    return;
                ExecutableRiskFlags? flags = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
                ApplyRiskFlags(flags ?? ExecutableRiskFlags.None);
            }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void ApplyRiskFlags(ExecutableRiskFlags flags)
        {
            if (_riskLabel == null)
                return;

            var lines = ExecutableRiskAssessment.Describe(flags);
            int newHeight = lines.Count == 0 ? 0 : lines.Count * RiskLineHeight + RiskLabelGap;
            int delta = newHeight - _riskHeight;

            _riskLabel.Text = lines.Count == 0
                ? string.Empty
                : "⚠ " + string.Join(Environment.NewLine + "⚠ ", lines);
            _riskLabel.Height = newHeight;
            _riskLabel.Visible = lines.Count > 0;

            if (delta != 0)
            {
                statusLabel.Top += delta;
                Height += delta;
                // The AI panel remembers the collapsed height; keep it in step so a later
                // collapse lands on the right size.
                if (_aiPanelExpanded)
                    _aiCollapsedHeight += delta;
                _riskHeight = newHeight;
                PositionBottomRight();
            }
        }
    }
}
