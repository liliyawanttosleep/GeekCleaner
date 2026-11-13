using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace GeekCleaner
{
    /// <summary>列出扫描到的残留项，让用户勾选确认后再删除。</summary>
    class LeftoverForm : Form
    {
        readonly DarkList lv = new DarkList();
        readonly Label sum = new Label();
        readonly List<LeftoverItem> items;
        public List<LeftoverItem> Selected = new List<LeftoverItem>();

        static int Order(LeftKind k)
        {
            switch (k)
            {
                case LeftKind.Dir: return 0;
                case LeftKind.File: return 1;
                case LeftKind.Service: return 2;
                case LeftKind.Task: return 3;
                case LeftKind.RegKey: return 4;
                default: return 5;
            }
        }

        public LeftoverForm(string appName, List<LeftoverItem> found)
        {
            items = found.OrderBy(i => Order(i.Kind)).ToList();
            Text = "残留清理 - " + appName;
            Font = Theme.Ui;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            if (Theme.AppIcon != null) Icon = Theme.AppIcon;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Theme.S(1040), Theme.S(640));
            MinimumSize = new Size(Theme.S(760), Theme.S(420));
            MinimizeBox = false;
            ShowInTaskbar = false;
            HandleCreated += delegate { Theme.DarkTitle(this); };

            var head = new Panel { Dock = DockStyle.Top, Height = Theme.S(92), BackColor = Theme.Bg };
            var title = new Label
            {
                Text = "发现 " + items.Count + " 个与 “" + appName + "” 关联的残留项",
                Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold),
                ForeColor = Theme.Text, AutoSize = true, Location = new Point(Theme.S(24), Theme.S(18))
            };
            var sub = new Label
            {
                Text = "请检查并取消勾选想保留的项目。注册表项删除前会自动备份为 .reg 文件（双击即可恢复）；文件和文件夹将被永久删除。",
                ForeColor = Theme.Sub, AutoSize = true, Location = new Point(Theme.S(26), Theme.S(54))
            };
            head.Controls.Add(title);
            head.Controls.Add(sub);

            lv.Dock = DockStyle.Fill;
            lv.Columns.Add("项目", Theme.S(560));
            lv.Columns.Add("类型", Theme.S(96));
            lv.Columns.Add("说明", Theme.S(300));
            lv.FillColumn = 0;
            lv.CheckGetter = li => ((LeftoverItem)li.Tag).Checked;
            lv.CheckToggle = li => { var it = (LeftoverItem)li.Tag; it.Checked = !it.Checked; UpdateSum(); };
            lv.PillColor = (li, col) =>
            {
                if (col != 1) return Color.Empty;
                LeftKind k = ((LeftoverItem)li.Tag).Kind;
                return (k == LeftKind.RegKey || k == LeftKind.RegValue) ? Theme.Violet
                     : (k == LeftKind.Dir || k == LeftKind.File) ? Theme.Accent2 : Theme.Warn;
            };
            lv.BeginUpdate();
            foreach (var it in items)
            {
                var li = new ListViewItem(new[] { it.Display, it.TypeText, it.Detail });
                li.Tag = it;
                lv.Items.Add(li);
            }
            lv.EndUpdate();
            var wrap = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Theme.S(24), 0, Theme.S(16), 0), BackColor = Theme.Bg };
            wrap.Controls.Add(lv);

            var foot = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(64), BackColor = Theme.Bg };
            var bAll = new Btn("全选", null, 0);
            var bNone = new Btn("全不选", null, 0);
            var bDel = new Btn("彻底删除选中项", "", 1);
            var bCancel = new Btn("取消", null, 0);
            int y = Theme.S(15);
            bAll.Location = new Point(Theme.S(24), y);
            bNone.Location = new Point(bAll.Right + Theme.S(8), y);
            sum.AutoSize = true;
            sum.ForeColor = Theme.Sub;
            sum.Location = new Point(bNone.Right + Theme.S(16), y + Theme.S(8));
            bDel.Location = new Point(foot.Width - bDel.Width - Theme.S(24), y);
            bDel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            bCancel.Location = new Point(bDel.Left - bCancel.Width - Theme.S(8), y);
            bCancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            foot.Controls.AddRange(new Control[] { bAll, bNone, sum, bCancel, bDel });

            bAll.Click += delegate { SetAll(true); };
            bNone.Click += delegate { SetAll(false); };
            bCancel.Click += delegate { DialogResult = DialogResult.Cancel; };
            bDel.Click += delegate
            {
                Selected = items.Where(i => i.Checked).ToList();
                if (Selected.Count == 0) { MessageBox.Show(this, "没有勾选任何项目。", Text); return; }
                if (MessageBox.Show(this, "确定彻底删除选中的 " + Selected.Count + " 个项目吗？", "确认删除",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                DialogResult = DialogResult.OK;
            };

            Controls.Add(wrap);
            Controls.Add(head);
            Controls.Add(foot);
            UpdateSum();
            Shown += delegate { lv.Fit(); };
        }

        void SetAll(bool v)
        {
            foreach (var i in items) i.Checked = v;
            lv.Invalidate();
            UpdateSum();
        }

        void UpdateSum()
        {
            sum.Text = "已选 " + items.Count(i => i.Checked) + " / " + items.Count + " 项";
        }
    }
}
