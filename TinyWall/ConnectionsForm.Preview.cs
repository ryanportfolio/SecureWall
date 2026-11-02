#if DEBUG
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace pylorak.TinyWall
{
    // Debug-only /uipreview: sample rows in place of the live endpoint tables and the
    // service's firewall log, so the window renders without the service.
    internal partial class ConnectionsForm
    {
        internal IReadOnlyList<UiPreviewSamples.ActivityRow>? PreviewRows { get; set; }

        private bool PreviewFilled;

        // Filled once: the 1 s refresh would otherwise drop the selection and scroll to the top.
        private void FillPreviewRows(IReadOnlyList<UiPreviewSamples.ActivityRow> rows)
        {
            if (PreviewFilled)
                return;
            PreviewFilled = true;
            IconScanner.CancelScan();
            var itemColl = new List<ListViewItem>();
            foreach (UiPreviewSamples.ActivityRow row in rows)
            {
                int before = itemColl.Count;
                ConstructListItem(itemColl, ProcessInfo.CreatePreview(row.Pid, row.Path, row.Services),
                    row.Protocol, row.Local, row.Remote, row.Status, row.Timestamp, row.Direction);
                // The sample host name, not whatever this machine's DNS cache holds.
                if (itemColl.Count > before)
                    itemColl[before].SubItems[6].Text = row.RemoteText;
            }

            list.BeginUpdate();
            list.Items.Clear();
            list.Items.AddRange(itemColl.ToArray());
            list.EndUpdate();
            IconScanner.Rescan(list.Items.Cast<ListViewItem>().ToList(), list, IconList);
        }

        // Selects the first row whose status starts with the given text and opens the row
        // menu on it, as a right-click there would, pointing at Unblock.
        internal void ShowPreviewMenu(string status)
        {
            ListViewItem? item = list.Items.Cast<ListViewItem>()
                .FirstOrDefault(li => li.SubItems[7].Text.StartsWith(status, System.StringComparison.Ordinal));
            if (item == null)
                return;
            list.SelectedItems.Clear();
            item.Selected = true;
            item.Focused = true;
            Rectangle bounds = item.GetBounds(ItemBoundsPortion.Label);
            contextMenuStrip1.Show(list, new Point(bounds.Left + 40, bounds.Top + bounds.Height / 2));
            // Highlighted as when the pointer rests on it.
            mnuUnblock.Select();
        }

        internal ContextMenuStrip PreviewMenu => contextMenuStrip1;
    }
}
#endif
