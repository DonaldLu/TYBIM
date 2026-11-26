using System.Windows.Forms;

namespace TYBIM.AutoBuild
{
    internal sealed class LayerCheckList : ListView
    {
        protected override void WndProc(ref Message message)
        {
            const int LeftDown = 0x0201, LeftDoubleClick = 0x0203;
            if (message.Msg == LeftDown || message.Msg == LeftDoubleClick)
            {
                int position = unchecked((int)message.LParam.ToInt64());
                int y = (short)(position >> 16);
                var item = HitTest((short)position, y).Item;
                if (item == null && TopItem != null)
                    item = HitTest(TopItem.GetBounds(ItemBoundsPortion.Icon).Left + 1, y).Item;
                if (item != null)
                {
                    Focus();
                    item.Focused = item.Selected = true;
                    item.Checked = !item.Checked;
                    return;
                }
            }
            base.WndProc(ref message);
        }
    }
}
