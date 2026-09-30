using System;
using System.Drawing;

namespace pylorak.TinyWall.Prompting
{
    // The notice label keeps its designer height for the usual two-line texts and grows when
    // a longer one (the SID notice with a long service name and its sc.exe command) needs
    // more lines. Everything below it moves by the same delta, as with the risk label.
    internal sealed partial class BlockedConnectionPopup
    {
        private int _noticeBaseHeight;
        private int _noticeGrowth;

        // Called from ShowPrompt after the notice text is set and before positioning.
        private void FitNoticeLabel()
        {
            if (_noticeBaseHeight == 0)
                _noticeBaseHeight = noticeLabel.Height;

            // Measured at the label's current (already DPI-scaled) width.
            int wanted = noticeLabel.GetPreferredSize(new Size(noticeLabel.Width, 0)).Height;
            int delta = Math.Max(0, wanted - _noticeBaseHeight) - _noticeGrowth;
            if (delta == 0)
                return;

            noticeLabel.Height += delta;
            if (_riskLabel != null)
                _riskLabel.Top += delta;
            statusLabel.Top += delta;
            Height += delta;
            // The AI panel remembers the collapsed height; keep it in step.
            if (_aiPanelExpanded)
                _aiCollapsedHeight += delta;
            _noticeGrowth += delta;
        }
    }
}
