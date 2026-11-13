using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GeekCleaner
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Theme.Init();
            var f = new MainForm();
            if (args.Length == 2 && args[0] == "--shot") f.ShotDir = args[1]; // 开发用：自动截图后退出
            Application.Run(f);
        }
    }

    class MainForm : Form
    {
        readonly Sidebar side = new Sidebar();
        readonly Panel content = new Panel();
        readonly Panel pageApps = new Panel(), pageJunk = new Panel(), pageDisk = new Panel();
        readonly Label status = new Label(), cancel = new Label();
        readonly BusyBar busyBar = new BusyBar();
        readonly List<Btn> actionBtns = new List<Btn>();
        readonly ToolTip tips = new ToolTip();
        CancellationTokenSource cts = new CancellationTokenSource();
        bool isBusy;
        int lastReport;
        public string ShotDir;

        // 程序页
        readonly DarkList lv = new DarkList();
        SearchBox search;
        readonly Label appsSub = new Label();
        readonly List<Chip> kindChips = new List<Chip>();
        readonly Dictionary<AppEntry, ListViewItem> itemMap = new Dictionary<AppEntry, ListViewItem>();
        List<AppEntry> apps = new List<AppEntry>();
        int sortCol = 0, gen = 0, kindFilter = 0;
        bool sortAsc = true;

        // 垃圾页
        readonly ListBox catList = new ListBox();
        readonly DarkList fileList = new DarkList();
        readonly Label junkBig = new Label(), junkSmall = new Label(), adviceLbl = new Label();
        readonly List<Chip> driveChips = new List<Chip>();
        List<JunkCategory> junk = new List<JunkCategory>();
        JunkCategory curCat;

        // 空间分析页
        readonly AnalysisView diskView = new AnalysisView();
        readonly DarkList diskList = new DarkList();
        readonly Label diskSub = new Label();
        readonly List<Chip> diskDrives = new List<Chip>(), diskModes = new List<Chip>();
        DiskStats disk;
        int diskMode;

        static int S(int v) { return Theme.S(v); }

        public MainForm()
        {
            Text = "GeekCleaner";
            Font = Theme.Ui;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            if (Theme.AppIcon != null) Icon = Theme.AppIcon;
            ClientSize = new Size(S(1300), S(800));
            MinimumSize = new Size(S(1260), S(700));
            StartPosition = FormStartPosition.CenterScreen;
            HandleCreated += delegate { Theme.DarkTitle(this); };

            side.Items = new[] { "应用程序", "垃圾清理", "空间分析" };
            side.Glyphs = new[] { "", "", "" };
            side.Dock = DockStyle.Left;
            side.SelectedChanged += delegate { ShowPage(side.Selected); };

            var statusPanel = new Panel { Dock = DockStyle.Bottom, Height = S(32), BackColor = Theme.Side };
            status.Dock = DockStyle.Fill;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.Padding = new Padding(S(14), 0, 0, 0);
            status.ForeColor = Theme.Sub;
            status.AutoEllipsis = true;
            cancel.Dock = DockStyle.Right;
            cancel.Width = S(64);
            cancel.Text = "取消";
            cancel.TextAlign = ContentAlignment.MiddleCenter;
            cancel.ForeColor = Theme.Accent;
            cancel.Cursor = Cursors.Hand;
            cancel.Visible = false;
            cancel.Click += delegate { cts.Cancel(); status.Text = "正在取消…"; };
            busyBar.Dock = DockStyle.Top;
            statusPanel.Controls.Add(status);
            statusPanel.Controls.Add(cancel);
            statusPanel.Controls.Add(busyBar);

            content.Dock = DockStyle.Fill;
            content.BackColor = Theme.Bg;
            BuildApps();
            BuildJunk();
            BuildDisk();
            content.Controls.Add(pageApps);
            content.Controls.Add(pageJunk);
            content.Controls.Add(pageDisk);
            pageJunk.Visible = false;
            pageDisk.Visible = false;

            Controls.Add(content);
            Controls.Add(statusPanel);
            Controls.Add(side);
            Shown += delegate { LoadApps(); if (ShotDir != null) RunShots(); };
        }

        void ShowPage(int i)
        {
            pageApps.Visible = i == 0;
            pageJunk.Visible = i == 1;
            pageDisk.Visible = i == 2;
            if (i == 0) lv.Fit(); else if (i == 1) fileList.Fit(); else diskList.Fit();
        }

        Btn Action(string text, string glyph, int kind, EventHandler h, string tip)
        {
            var b = new Btn(text, glyph, kind);
            b.Click += delegate(object o, EventArgs e) { if (!isBusy) h(o, e); };
            if (tip != null) tips.SetToolTip(b, tip);
            actionBtns.Add(b);
            return b;
        }

        static Label Lbl(string text, Font f, Color c, int x, int y)
        {
            return new Label { Text = text, Font = f, ForeColor = c, BackColor = Theme.Bg, AutoSize = true, Location = new Point(x, y) };
        }

        ContextMenuStrip NewMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Renderer = new ToolStripProfessionalRenderer(new DarkColors());
            menu.ShowImageMargin = false;
            menu.BackColor = Theme.Panel;
            menu.ForeColor = Theme.Text;
            menu.Font = Theme.Ui;
            return menu;
        }

        void SetBusy(bool busy, string msg)
        {
            if (busy) cts = new CancellationTokenSource();
            isBusy = busy;
            busyBar.Active = busy;
            cancel.Visible = busy;
            status.Text = msg;
            foreach (Btn b in actionBtns) b.Enabled = !busy;
            if (!busy) side.RefreshDrives();
        }

        void Report(string s)
        {
            int t = Environment.TickCount;
            if (unchecked(t - lastReport) < 80) return;
            lastReport = t;
            if (s.Length > 160) s = s.Substring(0, 160) + "…";
            try { BeginInvoke((Action)delegate { if (isBusy) status.Text = s; }); } catch { }
        }

        static void Reveal(string path)
        {
            try
            {
                if (File.Exists(path)) Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else if (Directory.Exists(path)) Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch { }
        }

        // =====================================================================
        //  程序页
        // =====================================================================

        void BuildApps()
        {
            pageApps.Dock = DockStyle.Fill;
            pageApps.BackColor = Theme.Bg;
            var head = new Panel { Dock = DockStyle.Top, Height = S(134), BackColor = Theme.Bg, Width = S(1000) };
            head.Controls.Add(Lbl("应用程序", Theme.Title, Theme.Text, S(24), S(18)));
            appsSub.ForeColor = Theme.Sub;
            appsSub.BackColor = Theme.Bg;
            appsSub.AutoSize = true;
            appsSub.Location = new Point(S(28), S(58));
            appsSub.Text = "正在检索…";
            head.Controls.Add(appsSub);

            var bBackup = Action("", "", 3, delegate
            {
                Directory.CreateDirectory(Util.BackupRoot);
                Process.Start("explorer.exe", "\"" + Util.BackupRoot + "\"");
            }, "打开注册表备份文件夹");
            var bRefresh = Action("", "", 3, delegate { LoadApps(); }, "重新检索程序列表");
            search = new SearchBox("搜索名称或发布者");
            search.Box.TextChanged += delegate { FillList(); };
            int x = head.Width - S(20), y = S(22);
            foreach (Control c in new Control[] { bBackup, bRefresh, search })
            {
                x -= c.Width;
                c.Location = new Point(x, y);
                c.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                head.Controls.Add(c);
                x -= S(6);
            }

            Btn[] row = {
                Action("卸载并清理", "", 1, delegate { DoUninstall(false); }, "对选中的一个或多个程序：运行卸载程序，然后深度扫描并清除残留"),
                Action("强制删除", "", 2, delegate { DoUninstall(true); }, "不运行卸载程序，直接扫描并删除选中程序的全部关联项"),
                Action("更新", "", 0, delegate { UpdateSelected(); }, "用 winget 更新选中的程序"),
                Action("检查更新", "", 0, delegate { Winget("upgrade --include-unknown"); }, "列出所有可更新的程序"),
                Action("打开位置", "", 0, delegate { OpenLocation(); }, "在资源管理器中打开安装目录")
            };
            x = S(28);
            y = S(88);
            foreach (Btn b in row)
            {
                b.Location = new Point(x, y);
                head.Controls.Add(b);
                x += b.Width + S(8);
            }

            string[] kinds = { "全部", "桌面程序", "便携/未注册", "商店应用", "建议卸载" };
            x = head.Width - S(20);
            for (int i = kinds.Length - 1; i >= 0; i--)
            {
                var c = new Chip(kinds[i]);
                c.Radio = true;
                c.Checked = i == 0;
                c.Tag = i;
                x -= c.Width;
                c.Location = new Point(x, y + S(3));
                c.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                c.Changed += delegate(object o, EventArgs e)
                {
                    kindFilter = (int)((Chip)o).Tag;
                    foreach (Chip k in kindChips) if (k != o) k.Checked = false;
                    FillList();
                };
                kindChips.Add(c);
                head.Controls.Add(c);
                x -= S(6);
            }

            lv.Dock = DockStyle.Fill;
            lv.MultiSelect = true;
            lv.Columns.Add("名称", S(320));
            lv.Columns.Add("版本", S(116));
            lv.Columns.Add("发布者", S(160));
            lv.Columns.Add("大小", S(88), HorizontalAlignment.Right);
            lv.Columns.Add("上次使用", S(96), HorizontalAlignment.Right);
            lv.Columns.Add("类型", S(108));
            lv.Columns.Add("建议", S(104));
            lv.FillColumn = 0;
            lv.SortColumn = 0;
            lv.EmptyText = "没有匹配的程序";
            lv.IconGetter = li =>
            {
                var a = (AppEntry)li.Tag;
                return (a.IconImg as Image) ?? Theme.LetterIcon(a.Name);
            };
            lv.PillColor = (li, col) =>
            {
                var a = (AppEntry)li.Tag;
                if (col == 5)
                {
                    switch (a.Kind)
                    {
                        case AppKind.Portable: return Theme.Warn;
                        case AppKind.Store: return Theme.Violet;
                        case AppKind.User: return Theme.Accent;
                        default: return Theme.Accent2;
                    }
                }
                if (col == 6)
                {
                    switch (a.Advice)
                    {
                        case 1: return Theme.Danger;
                        case 2: return Theme.Warn;
                        case 3: return Theme.Accent;
                        case 4: return Theme.Sub;
                    }
                }
                return Color.Empty;
            };
            lv.ColumnClick += delegate(object o, ColumnClickEventArgs e)
            {
                if (sortCol == e.Column) sortAsc = !sortAsc; else { sortCol = e.Column; sortAsc = e.Column != 3; }
                lv.SortColumn = sortCol;
                lv.SortAsc = sortAsc;
                SortApps();
                FillList();
            };
            lv.DoubleClick += delegate { if (!isBusy) DoUninstall(false); };
            lv.SelectedIndexChanged += delegate
            {
                if (isBusy || lv.SelectedItems.Count == 0) return;
                if (lv.SelectedItems.Count > 1)
                {
                    long sum = 0;
                    foreach (ListViewItem li in lv.SelectedItems) { long z = ((AppEntry)li.Tag).Size; if (z > 0) sum += z; }
                    status.Text = "已选中 " + lv.SelectedItems.Count + " 个程序" + (sum > 0 ? "，合计约 " + Util.FormatSize(sum) : "") + "。点击「卸载并清理」或「强制删除」可批量处理（Ctrl / Shift 多选）。";
                    return;
                }
                var a = (AppEntry)lv.SelectedItems[0].Tag;
                string t = a.Name;
                if (a.InstallLocation != null) t += "   ·   " + a.InstallLocation + (a.LocationGuessed ? "（位置为推断）" : "");
                if (a.IdleDays >= 0) t += "   ·   上次启动 " + a.LastUsed.ToString("yyyy-MM-dd") + "，共 " + a.RunCount + " 次";
                if (a.InstallDate.Length > 0) t += "   ·   安装于 " + a.InstallDate;
                if (a.AdviceWhy.Length > 0) t += "   ·   " + a.AdviceWhy;
                status.Text = t;
            };

            var menu = NewMenu();
            menu.Opening += delegate(object o, System.ComponentModel.CancelEventArgs e) { e.Cancel = isBusy || lv.SelectedItems.Count == 0; };
            menu.Items.Add("卸载并清除残留（可多选）", null, delegate { DoUninstall(false); });
            menu.Items.Add("强制删除（不运行卸载程序，可多选）", null, delegate { DoUninstall(true); });
            menu.Items.Add("修改 / 修复", null, delegate { Modify(); });
            menu.Items.Add("更新 (winget)", null, delegate { UpdateSelected(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("打开安装位置", null, delegate { OpenLocation(); });
            menu.Items.Add("打开注册表项", null, delegate { OpenRegedit(); });
            menu.Items.Add("仅从列表移除条目", null, delegate { RemoveEntry(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("在网上搜索该程序", null, delegate
            {
                var a = Sel();
                if (a != null) Process.Start("https://www.bing.com/search?q=" + Uri.EscapeDataString(a.Name));
            });
            foreach (ToolStripItem mi in menu.Items) mi.ForeColor = Theme.Text;
            lv.ContextMenuStrip = menu;

            var wrap = new Panel { Dock = DockStyle.Fill, Padding = new Padding(S(24), 0, S(14), S(6)), BackColor = Theme.Bg };
            wrap.Controls.Add(lv);
            pageApps.Controls.Add(wrap);
            pageApps.Controls.Add(head);
        }

        List<AppEntry> SelAll()
        {
            var l = new List<AppEntry>();
            foreach (ListViewItem li in lv.SelectedItems) l.Add((AppEntry)li.Tag);
            if (l.Count == 0)
                MessageBox.Show(this, "请先在列表中选择程序（按住 Ctrl 或 Shift 可以多选）。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return l;
        }

        AppEntry Sel()
        {
            var l = SelAll();
            return l.Count == 0 ? null : l[0];
        }

        async void LoadApps()
        {
            if (isBusy) return;
            SetBusy(true, "正在检索已安装的程序（注册表、程序文件夹、商店应用、使用记录）…");
            int g = ++gen;
            List<AppEntry> list;
            try { list = await Task.Run(() => AppScanner.ScanAll()); }
            catch (Exception ex) { SetBusy(false, "检索失败: " + ex.Message); return; }
            apps = list;
            SortApps();
            FillList();
            SetBusy(false, "检索完成，共 " + apps.Count + " 个程序。按住 Ctrl / Shift 可多选后批量卸载；「建议」列综合了使用频率和占用大小。");
            LoadDetails(g, list);
        }

        void SortApps()
        {
            Comparison<AppEntry> c;
            switch (sortCol)
            {
                case 1: c = (a, b) => string.Compare(a.Version, b.Version, StringComparison.CurrentCultureIgnoreCase); break;
                case 2: c = (a, b) => string.Compare(a.Publisher, b.Publisher, StringComparison.CurrentCultureIgnoreCase); break;
                case 3: c = (a, b) => a.Size.CompareTo(b.Size); break;
                case 4: c = (a, b) => a.LastUsed.CompareTo(b.LastUsed); break;
                case 5: c = (a, b) => a.Kind.CompareTo(b.Kind); break;
                case 6: c = (a, b) => (a.Advice == 0 ? 9 : a.Advice).CompareTo(b.Advice == 0 ? 9 : b.Advice); break;
                default: c = (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); break;
            }
            if (sortAsc) apps.Sort(c); else apps.Sort((a, b) => c(b, a));
        }

        bool KindOk(AppEntry a)
        {
            switch (kindFilter)
            {
                case 1: return a.Kind != AppKind.Portable && a.Kind != AppKind.Store;
                case 2: return a.Kind == AppKind.Portable;
                case 3: return a.Kind == AppKind.Store;
                case 4: return a.Advice == 1 || a.Advice == 2;
                default: return true;
            }
        }

        static string[] Row(AppEntry a)
        {
            return new[] { a.Name, a.Version, a.Publisher, Util.FormatSize(a.Size), a.UsedText, a.KindText, a.AdviceText };
        }

        void FillList()
        {
            string q = search.Box.Text.Trim();
            lv.BeginUpdate();
            lv.Items.Clear();
            itemMap.Clear();
            foreach (var a in apps)
            {
                if (!KindOk(a)) continue;
                if (q.Length > 0 && a.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) < 0 &&
                    a.Publisher.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) < 0) continue;
                var li = new ListViewItem(Row(a));
                li.Tag = a;
                lv.Items.Add(li);
                itemMap[a] = li;
            }
            lv.EndUpdate();
            lv.Fit();
            UpdateAppsSub();
        }

        void UpdateAppsSub()
        {
            long total = 0, sug = 0;
            int nSug = 0;
            foreach (ListViewItem li in lv.Items) { long s = ((AppEntry)li.Tag).Size; if (s > 0) total += s; }
            foreach (var a in apps) if (a.Advice == 1 || a.Advice == 2) { nSug++; if (a.Size > 0) sug += a.Size; }
            appsSub.Text = "共 " + apps.Count + " 个程序，当前显示 " + lv.Items.Count + " 个，合计约 " + Util.FormatSize(total) +
                           (nSug > 0 ? "   ·   其中 " + nSug + " 个长期未使用" + (sug > 0 ? "，卸载可释放约 " + Util.FormatSize(sug) : "") : "");
        }

        /// <summary>后台加载图标并计算缺失的大小。</summary>
        void LoadDetails(int g, List<AppEntry> list)
        {
            Task.Run(delegate
            {
                int n = 0;
                foreach (var a in list)
                {
                    if (g != gen || IsDisposed) return;
                    Bitmap bmp = null;
                    try
                    {
                        string p = a.DisplayIcon;
                        if (p != null) p = Util.Norm(Regex.Replace(p.Trim().Trim('"'), @",\s*-?\d+$", ""));
                        if ((p == null || !File.Exists(p)) && a.InstallLocation != null && a.Kind != AppKind.Store)
                        {
                            try { p = Directory.GetFiles(a.InstallLocation, "*.exe").FirstOrDefault(); } catch { p = null; }
                        }
                        if (p != null && File.Exists(p))
                            using (var ico = Icon.ExtractAssociatedIcon(p)) bmp = ico.ToBitmap();
                    }
                    catch { }
                    long size = a.Size;
                    if (size < 0 && a.InstallLocation != null && !a.LocationGuessed && a.Kind != AppKind.Store)
                        size = Util.DirSize(a.InstallLocation, CancellationToken.None);
                    AppEntry app = a;
                    bool last = ++n == list.Count;
                    try
                    {
                        BeginInvoke((Action)delegate
                        {
                            if (g != gen) return;
                            app.Size = size;
                            if (bmp != null) app.IconImg = bmp;
                            AppScanner.Advise(app);
                            ListViewItem li;
                            if (itemMap.TryGetValue(app, out li) && li.ListView != null)
                            {
                                li.SubItems[3].Text = Util.FormatSize(size);
                                li.SubItems[6].Text = app.AdviceText;
                                lv.Invalidate(li.Bounds);
                            }
                            if (last) UpdateAppsSub();
                        });
                    }
                    catch { return; }
                }
            });
        }

        void OpenLocation()
        {
            var a = Sel();
            if (a == null) return;
            if (a.InstallLocation != null && Directory.Exists(a.InstallLocation))
                Process.Start("explorer.exe", "\"" + a.InstallLocation + "\"");
            else MessageBox.Show(this, "未找到该程序的安装位置。", "提示");
        }

        void OpenRegedit()
        {
            var a = Sel();
            if (a == null) return;
            if (a.RegPath == null) { MessageBox.Show(this, "该程序没有卸载注册表项。", "提示"); return; }
            try
            {
                string full = (a.RegHive == "HKLM" ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER") + "\\" + a.RegPath;
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
                    k.SetValue("LastKey", full);
                Process.Start("regedit.exe", "/m");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "错误"); }
        }

        void Modify()
        {
            var a = Sel();
            if (a == null) return;
            try
            {
                string exe, args;
                if (a.IsMsi && a.KeyName.StartsWith("{")) Process.Start("msiexec.exe", "/i " + a.KeyName);
                else if (Util.SplitCommand(a.ModifyPath, out exe, out args)) Process.Start(exe, args);
                else MessageBox.Show(this, "该程序没有提供修改/修复功能。", "提示");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "错误"); }
        }

        void Winget(string args)
        {
            try { Process.Start("cmd.exe", "/k title GeekCleaner - winget & winget " + args); }
            catch (Exception ex) { MessageBox.Show(this, "无法启动 winget: " + ex.Message, "错误"); }
        }

        void UpdateSelected()
        {
            var a = Sel();
            if (a == null) return;
            if (a.Kind == AppKind.Store) { Process.Start("ms-windows-store://downloadsandupdates"); return; }
            if (a.Kind == AppKind.Portable)
            {
                MessageBox.Show(this, "便携/未注册程序无法通过 winget 更新，请到官网下载新版本。", "提示");
                return;
            }
            Winget("upgrade --name \"" + a.Name.Replace("\"", "") + "\"");
        }

        async void RemoveEntry()
        {
            var a = Sel();
            if (a == null) return;
            if (a.RegPath == null) { MessageBox.Show(this, "该项目没有注册表条目。", "提示"); return; }
            if (MessageBox.Show(this, "只删除 “" + a.Name + "” 在“已安装程序”列表中的注册表条目（会先备份），不删除任何文件。继续吗？",
                    "移除条目", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var items = new List<LeftoverItem> { new LeftoverItem { Kind = LeftKind.RegKey, Hive = a.RegHive, KeyPath = a.RegPath } };
            var r = await Task.Run(() => LeftoverScanner.Delete(items, a.Name, null));
            if (r.Failed.Count > 0) MessageBox.Show(this, string.Join("\r\n", r.Failed), "删除失败");
            LoadApps();
        }

        static void RunUninstaller(AppEntry a)
        {
            if (a.IsMsi && a.KeyName.StartsWith("{"))
            {
                Util.RunWait("msiexec.exe", "/x " + a.KeyName, false);
                return;
            }
            string exe, args;
            if (Util.SplitCommand(a.UninstallString, out exe, out args) && File.Exists(Environment.ExpandEnvironmentVariables(exe)))
            {
                var psi = new ProcessStartInfo(Environment.ExpandEnvironmentVariables(exe), args);
                psi.UseShellExecute = true;
                using (var p = Process.Start(psi)) if (p != null) p.WaitForExit();
            }
            else
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c \"" + a.UninstallString + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (var p = Process.Start(psi)) p.WaitForExit();
            }
        }

        static bool KeyExists(AppEntry a)
        {
            if (a.RegHive == null || a.RegPath == null) return false;
            try
            {
                using (var bk = LeftoverScanner.OpenHive(a.RegHive))
                using (var k = bk.OpenSubKey(a.RegPath)) return k != null;
            }
            catch { return false; }
        }

        static string NameList(IEnumerable<AppEntry> list)
        {
            var names = list.Select(a => "  • " + a.Name).ToList();
            if (names.Count > 14) { int more = names.Count - 12; names = names.Take(12).ToList(); names.Add("  … 以及另外 " + more + " 个"); }
            return string.Join("\r\n", names);
        }

        /// <summary>卸载选中的一个或多个程序，并统一扫描、确认、清除残留。</summary>
        async void DoUninstall(bool force)
        {
            var all = SelAll();
            if (all.Count == 0) return;
            var store = all.Where(a => a.Kind == AppKind.Store).ToList();
            var normal = all.Where(a => a.Kind != AppKind.Store).ToList();
            bool many = all.Count > 1;
            string who = many ? "选中的 " + all.Count + " 个程序" : "“" + all[0].Name + "”";

            string msg;
            if (normal.Count == 0)
                msg = "确定卸载" + who + "吗？\r\n商店应用及其数据将由系统一并移除。";
            else if (force)
                msg = "将【强制删除】" + who + "：\r\n\r\n不运行程序自带的卸载程序，直接扫描安装目录、注册表项、服务、计划任务、快捷方式和用户数据，列出后由你确认删除。";
            else
                msg = "将卸载" + who + "：\r\n\r\n1. 依次运行各程序自带的卸载程序\r\n2. 然后深度扫描注册表和磁盘上的全部残留\r\n3. 汇总列出残留，由你确认后彻底清除";
            if (many) msg += "\r\n\r\n" + NameList(all);
            if (MessageBox.Show(this, msg + "\r\n\r\n继续吗？", force ? "强制删除" : "卸载", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

            // ---- 商店应用：直接交给系统卸载 ----
            var problems = new List<string>();
            for (int i = 0; i < store.Count; i++)
            {
                AppEntry a = store[i];
                SetBusy(true, "正在卸载商店应用 (" + (i + 1) + "/" + store.Count + "): " + a.Name);
                int code = await Task.Run(() => Util.RunWait("powershell.exe",
                    "-NoProfile -NonInteractive -Command \"Remove-AppxPackage -Package '" + a.PackageFullName + "'\"", true));
                if (code != 0) problems.Add(a.Name + "（商店应用卸载失败）");
            }
            if (normal.Count == 0)
            {
                SetBusy(false, "商店应用卸载完成。");
                if (problems.Count > 0) MessageBox.Show(this, string.Join("\r\n", problems), "部分未成功", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                LoadApps();
                return;
            }

            // 卸载前先记录信息（卸载后注册表项就没了）
            var jobs = normal.Select(a => Tuple.Create(a, new LeftoverScanner(a))).ToList();

            if (!force)
            {
                var withUn = normal.Where(a => !string.IsNullOrWhiteSpace(a.UninstallString)).ToList();
                for (int i = 0; i < withUn.Count; i++)
                {
                    AppEntry a = withUn[i];
                    SetBusy(true, "正在运行卸载程序 (" + (i + 1) + "/" + withUn.Count + "): " + a.Name + " —— 请在弹出的向导中完成卸载…");
                    try { await Task.Run(() => RunUninstaller(a)); }
                    catch (Exception ex) { problems.Add(a.Name + "（卸载程序无法运行: " + ex.Message + "）"); }
                    if (cts.IsCancellationRequested) break;
                }
                SetBusy(false, "");
                if (withUn.Count > 0 && MessageBox.Show(this, "请等待所有卸载向导完全结束。\r\n\r\n完成后点击“确定”开始扫描残留；如果想中止，请点击“取消”。",
                        "等待卸载完成", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) { LoadApps(); return; }

                var still = normal.Where(KeyExists).ToList();
                if (still.Count > 0)
                {
                    var dr = MessageBox.Show(this, "以下 " + still.Count + " 个程序的卸载信息仍然存在，可能没有卸载完成：\r\n\r\n" + NameList(still) +
                        "\r\n\r\n“是” = 仍把它们当作残留强制清除\r\n“否” = 跳过这些程序，只处理已卸载的\r\n“取消” = 全部中止",
                        "卸载可能未完成", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                    if (dr == DialogResult.Cancel) { LoadApps(); return; }
                    if (dr == DialogResult.No) jobs = jobs.Where(j => !still.Contains(j.Item1)).ToList();
                }
                if (jobs.Count == 0) { LoadApps(); return; }
            }

            // ---- 逐个深度扫描残留，汇总 ----
            SetBusy(true, "正在深度扫描注册表和磁盘残留…");
            var found = new List<LeftoverItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                CancellationToken tk = cts.Token;
                for (int i = 0; i < jobs.Count; i++)
                {
                    var job = jobs[i];
                    string prefix = jobs.Count > 1 ? "(" + (i + 1) + "/" + jobs.Count + ") " + job.Item1.Name + " — " : "";
                    var part = await Task.Run(() => job.Item2.Scan(s => Report(prefix + s), tk));
                    foreach (var it in part)
                    {
                        if (!seen.Add(it.Display)) continue;
                        it.Owner = job.Item1.Name;
                        if (jobs.Count > 1) it.Detail = "[" + job.Item1.Name + "] " + it.Detail;
                        found.Add(it);
                    }
                }
            }
            catch (OperationCanceledException) { SetBusy(false, "已取消扫描。"); LoadApps(); return; }
            catch (Exception ex) { SetBusy(false, "扫描出错: " + ex.Message); return; }
            SetBusy(false, "扫描完成，发现 " + found.Count + " 个残留项。");

            string title = jobs.Count == 1 ? jobs[0].Item1.Name : jobs.Count + " 个程序";
            if (found.Count == 0)
            {
                MessageBox.Show(this, "没有发现任何残留，" + title + " 已清理干净。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                LoadApps();
                return;
            }
            using (var f = new LeftoverForm(title, found))
            {
                if (f.ShowDialog(this) != DialogResult.OK) { LoadApps(); return; }
                var sel = f.Selected;
                SetBusy(true, "正在清除残留…");
                var r = await Task.Run(() => LeftoverScanner.Delete(sel, title, Report));
                SetBusy(false, "清除完成。");
                r.Failed.InsertRange(0, problems);
                string text = "已清除 " + r.Done + " 个项目，释放磁盘空间 " + Util.FormatSize(r.Freed) + "。";
                if (r.BackupFile != null) text += "\r\n\r\n注册表备份: " + r.BackupFile;
                if (r.Failed.Count > 0)
                    text += "\r\n\r\n以下 " + r.Failed.Count + " 项未能立即处理:\r\n" + string.Join("\r\n", r.Failed.Take(15)) +
                            (r.Failed.Count > 15 ? "\r\n…" : "");
                MessageBox.Show(this, text, "清除结果", MessageBoxButtons.OK, r.Failed.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
            LoadApps();
        }

        // =====================================================================
        //  垃圾清理页
        // =====================================================================

        static readonly Color[] LevelColors = { Theme.Accent, Theme.Accent2, Theme.Warn, Theme.Sub };

        void BuildJunk()
        {
            pageJunk.Dock = DockStyle.Fill;
            pageJunk.BackColor = Theme.Bg;
            var head = new Panel { Dock = DockStyle.Top, Height = S(180), BackColor = Theme.Bg, Width = S(1000) };
            head.Controls.Add(Lbl("垃圾清理", Theme.Title, Theme.Text, S(24), S(18)));
            head.Controls.Add(Lbl("每个文件都按「重要程度 + 闲置时间 + 占用大小」综合评分，分成四个清理优先级。扫描只会列出，删除什么由你决定。", Theme.Ui, Theme.Sub, S(28), S(58)));

            junkBig.Font = Theme.Big;
            junkBig.ForeColor = Theme.Accent;
            junkBig.BackColor = Theme.Bg;
            junkBig.TextAlign = ContentAlignment.MiddleRight;
            junkBig.SetBounds(head.Width - S(300), S(6), S(280), S(50));
            junkBig.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            junkBig.Text = "—";
            junkSmall.ForeColor = Theme.Sub;
            junkSmall.BackColor = Theme.Bg;
            junkSmall.TextAlign = ContentAlignment.MiddleRight;
            junkSmall.SetBounds(head.Width - S(480), S(92), S(460), S(24));
            junkSmall.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            junkSmall.Text = "尚未扫描";
            head.Controls.Add(junkBig);
            head.Controls.Add(junkSmall);

            int x = S(28), y = S(88);
            var bScan = Action("开始扫描", "", 1, delegate { ScanJunk(); }, "扫描所选磁盘上的垃圾文件");
            bScan.Location = new Point(x, y);
            head.Controls.Add(bScan);
            x += bScan.Width + S(8);
            var bClean = Action("清理选中项", "", 2, delegate { CleanJunk(); }, "一次性永久删除所有已勾选的垃圾文件");
            bClean.Location = new Point(x, y);
            head.Controls.Add(bClean);
            x += bClean.Width + S(20);
            var dl = Lbl("扫描范围", Theme.Ui, Theme.Sub, x, y + S(8));
            head.Controls.Add(dl);
            x += dl.PreferredWidth + S(10);
            foreach (string d in Util.FixedDrives())
            {
                var c = new Chip(d.TrimEnd('\\'));
                c.Tag = d;
                c.Checked = d.StartsWith("C", StringComparison.OrdinalIgnoreCase) || d.StartsWith("D", StringComparison.OrdinalIgnoreCase);
                c.Location = new Point(x, y + S(3));
                x += c.Width + S(6);
                driveChips.Add(c);
                head.Controls.Add(c);
            }

            // 第三行：按建议一键勾选
            x = S(28);
            y = S(134);
            var ql = Lbl("按建议勾选", Theme.Ui, Theme.Sub, x, y + S(8));
            head.Controls.Add(ql);
            x += ql.PreferredWidth + S(10);
            string[] names = { "仅优先清理", "优先 + 建议", "全部可清理", "全不选" };
            int[] levels = { 0, 1, 2, -1 };
            string[] tipsText = { "只勾选评分最高、可以放心删除的项目", "勾选「优先清理」和「建议清理」两级", "勾选除「自行判断」以外的全部项目", "取消全部勾选" };
            for (int i = 0; i < names.Length; i++)
            {
                int lvl = levels[i];
                var b = Action(names[i], null, 0, delegate { ApplyLevel(lvl); }, tipsText[i]);
                b.Location = new Point(x, y);
                head.Controls.Add(b);
                x += b.Width + S(6);
            }
            adviceLbl.ForeColor = Theme.Sub;
            adviceLbl.BackColor = Theme.Bg;
            adviceLbl.AutoEllipsis = true;
            adviceLbl.TextAlign = ContentAlignment.MiddleLeft;
            adviceLbl.SetBounds(x + S(10), y, head.Width - x - S(30), S(34));
            adviceLbl.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            head.Controls.Add(adviceLbl);

            catList.DrawMode = DrawMode.OwnerDrawFixed;
            catList.ItemHeight = S(74);
            catList.BorderStyle = BorderStyle.None;
            catList.BackColor = Theme.Bg;
            catList.IntegralHeight = false;
            catList.Dock = DockStyle.Left;
            catList.Width = S(380);
            catList.DrawItem += DrawCat;
            catList.HandleCreated += delegate { Theme.DarkScroll(catList); };
            catList.SelectedIndexChanged += delegate { ShowCategory(catList.SelectedItem as JunkCategory); };
            catList.MouseDown += delegate(object o, MouseEventArgs e)
            {
                int i = catList.IndexFromPoint(e.Location);
                if (i < 0 || e.X > S(46) || isBusy) return;
                var c = (JunkCategory)catList.Items[i];
                bool all = c.Items.All(it => it.Checked);
                foreach (var it in c.Items) it.Checked = !all;
                JunkChanged();
            };

            fileList.Dock = DockStyle.Fill;
            fileList.VirtualMode = true;
            fileList.MultiSelect = true;
            fileList.Columns.Add("文件 / 文件夹（Ctrl / Shift 多选，空格批量勾选）", S(400));
            fileList.Columns.Add("清理建议", S(96));
            fileList.Columns.Add("闲置", S(78), HorizontalAlignment.Right);
            fileList.Columns.Add("大小", S(96), HorizontalAlignment.Right);
            fileList.FillColumn = 0;
            fileList.EmptyText = "点击「开始扫描」，查找所选磁盘上可以清理的垃圾文件";
            fileList.RetrieveVirtualItem += delegate(object o, RetrieveVirtualItemEventArgs e)
            {
                JunkItem it = (curCat != null && e.ItemIndex < curCat.Items.Count) ? curCat.Items[e.ItemIndex] : new JunkItem { FsPath = "" };
                e.Item = new ListViewItem(new[] { it.FsPath, JunkItem.LevelNames[it.Level], it.IdleText, Util.FormatSize(it.Size) });
                e.Item.Tag = it;
            };
            fileList.PillColor = (li, col) => col == 1 ? LevelColors[((JunkItem)li.Tag).Level] : Color.Empty;
            fileList.CheckGetter = li => ((JunkItem)li.Tag).Checked;
            fileList.CheckToggle = li =>
            {
                if (isBusy) return;
                var it = (JunkItem)li.Tag;
                it.Checked = !it.Checked;
                catList.Invalidate();
                UpdateJunkSum();
            };
            fileList.DoubleClick += delegate
            {
                var it = FirstJunkSel();
                if (it != null && it.RecycleDrive == null) Reveal(it.FsPath);
            };
            fileList.SelectedIndexChanged += delegate
            {
                if (isBusy || curCat == null) return;
                int n = fileList.SelectedIndices.Count;
                if (n == 1)
                {
                    var it = FirstJunkSel();
                    if (it != null) status.Text = JunkItem.LevelNames[it.Level] + "   ·   " + it.CatName + "   ·   " + it.Reason + "   ·   " + it.FsPath;
                }
                else if (n > 1) status.Text = "已选中 " + n + " 行。按空格键，或右键选择「勾选所选项 / 取消勾选所选项」进行批量操作。";
            };
            var fm = NewMenu();
            fm.Opening += delegate(object o, System.ComponentModel.CancelEventArgs e) { e.Cancel = isBusy || fileList.SelectedIndices.Count == 0; };
            fm.Items.Add("勾选所选项", null, delegate { CheckSelected(true); });
            fm.Items.Add("取消勾选所选项", null, delegate { CheckSelected(false); });
            fm.Items.Add(new ToolStripSeparator());
            fm.Items.Add("在资源管理器中显示", null, delegate { var it = FirstJunkSel(); if (it != null && it.RecycleDrive == null) Reveal(it.FsPath); });
            foreach (ToolStripItem mi in fm.Items) mi.ForeColor = Theme.Text;
            fileList.ContextMenuStrip = fm;

            var wrap = new Panel { Dock = DockStyle.Fill, Padding = new Padding(S(24), 0, S(14), S(6)), BackColor = Theme.Bg };
            wrap.Controls.Add(fileList);
            wrap.Controls.Add(new Panel { Dock = DockStyle.Left, Width = S(14), BackColor = Theme.Bg });
            wrap.Controls.Add(catList);
            pageJunk.Controls.Add(wrap);
            pageJunk.Controls.Add(head);
        }

        JunkItem FirstJunkSel()
        {
            if (curCat == null || fileList.SelectedIndices.Count == 0) return null;
            int i = fileList.SelectedIndices[0];
            return i < curCat.Items.Count ? curCat.Items[i] : null;
        }

        void CheckSelected(bool v)
        {
            if (isBusy || curCat == null) return;
            foreach (int i in fileList.SelectedIndices)
                if (i < curCat.Items.Count) curCat.Items[i].Checked = v;
            JunkChanged();
        }

        void ApplyLevel(int maxLevel)
        {
            if (junk.Count == 0) { MessageBox.Show(this, "请先点击「开始扫描」。", "提示"); return; }
            foreach (var c in junk)
                foreach (var it in c.Items) it.Checked = it.Level <= maxLevel;
            JunkChanged();
        }

        void JunkChanged()
        {
            catList.Invalidate();
            fileList.Invalidate();
            UpdateJunkSum();
        }

        void DrawCat(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var c = (JunkCategory)catList.Items[e.Index];
            Graphics g = e.Graphics;
            Rectangle r = e.Bounds;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, r);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF card = new RectangleF(r.X + 1, r.Y + S(3), r.Width - S(8), r.Height - S(7));
            Theme.FillRound(g, sel ? Theme.PanelHi : Theme.Panel, card, S(10));
            Color tone = c.Virtual ? Theme.Violet : c.DefaultChecked ? Theme.Accent : Theme.Warn;
            if (sel) Theme.DrawRound(g, Color.FromArgb(120, tone), card, S(10));

            int n = 0;
            foreach (var it in c.Items) if (it.Checked) n++;
            int cs = S(18);
            Theme.DrawCheck(g, new Rectangle(r.X + S(16), r.Y + S(16), cs, cs), n == 0 ? 0 : n == c.Items.Count ? 1 : 2);

            const TextFormatFlags fl = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            int tx = r.X + S(48), right = (int)card.Right - S(14), rw = S(96);
            TextRenderer.DrawText(g, c.Name, Theme.UiBold, new Rectangle(tx, r.Y + S(13), right - rw - tx, S(20)), Theme.Text, fl);
            TextRenderer.DrawText(g, Util.FormatSize(c.Total), Theme.UiBold, new Rectangle(right - rw, r.Y + S(13), rw, S(20)), tone, fl | TextFormatFlags.Right);
            TextRenderer.DrawText(g, c.Hint, Theme.Small, new Rectangle(tx, r.Y + S(35), right - S(70) - tx, S(18)), Theme.Sub, fl);
            TextRenderer.DrawText(g, c.Items.Count + " 项", Theme.Small, new Rectangle(right - S(70), r.Y + S(35), S(70), S(18)), Theme.Sub, fl | TextFormatFlags.Right);

            // 底部细条：该分类中四个优先级各占多少（按大小）
            RectangleF bar = new RectangleF(tx, r.Y + S(59), right - tx, S(3));
            Theme.FillRound(g, Theme.Border, bar, bar.Height / 2);
            long[] lv4 = new long[4];
            foreach (var it in c.Items) lv4[it.Level] += it.Size;
            long tot = Math.Max(1, lv4.Sum());
            float bx = bar.X;
            for (int i = 0; i < 4; i++)
            {
                float bw = bar.Width * lv4[i] / tot;
                if (bw < 0.5f) continue;
                using (var b = new SolidBrush(LevelColors[i])) g.FillRectangle(b, bx, bar.Y, bw, bar.Height);
                bx += bw;
            }
        }

        void ShowCategory(JunkCategory c)
        {
            curCat = c;
            try
            {
                fileList.SelectedIndices.Clear();
                fileList.VirtualListSize = c == null ? 0 : c.Items.Count;
            }
            catch { }
            fileList.Fit();
            fileList.Invalidate();
        }

        void UpdateJunkSum()
        {
            long sel = 0, all = 0;
            int n = 0, total = 0;
            long[] ls = new long[4];
            int[] ln = new int[4];
            foreach (var c in junk)
                foreach (var i in c.Items)
                {
                    total++;
                    all += i.Size;
                    ls[i.Level] += i.Size;
                    ln[i.Level]++;
                    if (i.Checked) { n++; sel += i.Size; }
                }
            junkBig.Text = total == 0 ? "—" : Util.FormatSize(sel);
            junkSmall.Text = total == 0 ? "尚未扫描" : "已勾选 " + n + " 项可释放   ·   共发现 " + total + " 项 / " + Util.FormatSize(all);
            if (total == 0) { adviceLbl.Text = ""; return; }
            var parts = new List<string>();
            for (int i = 0; i < 4; i++)
                if (ln[i] > 0) parts.Add(JunkItem.LevelNames[i] + " " + ln[i] + " 项 · " + Util.FormatSize(ls[i]));
            adviceLbl.Text = string.Join("      ", parts);
        }

        void RebuildJunk()
        {
            string keep = curCat == null ? null : curCat.Name;
            catList.BeginUpdate();
            catList.Items.Clear();
            var real = junk.Where(c => c.Items.Count > 0).ToList();
            if (real.Count > 0) catList.Items.Add(JunkScanner.Overview(real));
            foreach (var c in real) catList.Items.Add(c);
            catList.EndUpdate();
            fileList.EmptyText = junk.Count == 0 ? "没有发现垃圾文件，磁盘很干净" : "";
            if (catList.Items.Count > 0)
            {
                int idx = 0;
                for (int i = 0; i < catList.Items.Count; i++) if (((JunkCategory)catList.Items[i]).Name == keep) idx = i;
                catList.SelectedIndex = idx;
                ShowCategory(catList.SelectedItem as JunkCategory);
            }
            else ShowCategory(null);
            UpdateJunkSum();
        }

        async void ScanJunk()
        {
            var drives = driveChips.Where(c => c.Checked).Select(c => (string)c.Tag).ToList();
            if (drives.Count == 0) { MessageBox.Show(this, "请至少选择一个磁盘。", "提示"); return; }
            SetBusy(true, "正在扫描垃圾文件…");
            var sw = Stopwatch.StartNew();
            try
            {
                CancellationToken tk = cts.Token;
                junk = await Task.Run(() => new JunkScanner().Scan(drives, Report, tk));
            }
            catch (OperationCanceledException) { SetBusy(false, "已取消扫描。"); return; }
            catch (Exception ex) { SetBusy(false, "扫描出错: " + ex.Message); return; }
            curCat = null;
            RebuildJunk();
            SetBusy(false, "扫描完成，用时 " + (int)sw.Elapsed.TotalSeconds + " 秒。「综合清理建议」已按优先级排好序；可用上方按钮一键勾选，再点击「清理选中项」。");
        }

        async void CleanJunk()
        {
            long size = 0;
            int n = 0;
            foreach (var c in junk)
                foreach (var i in c.Items)
                    if (i.Checked) { n++; size += i.Size; }
            if (n == 0) { MessageBox.Show(this, "没有勾选任何要清理的项目。请先扫描并勾选。", "提示"); return; }

            var names = junk.Where(c => c.Items.Any(i => i.Checked))
                            .Select(c => "  • " + c.Name + "（" + c.Items.Count(i => i.Checked) + " 项，" + Util.FormatSize(c.Items.Where(i => i.Checked).Sum(i => i.Size)) + "）");
            int risky = junk.Sum(c => c.Items.Count(i => i.Checked && i.Level == 3));
            string msg = "即将永久删除以下垃圾文件，共 " + n + " 项，" + Util.FormatSize(size) + "：\r\n\r\n" + string.Join("\r\n", names) +
                         (risky > 0 ? "\r\n\r\n注意：其中有 " + risky + " 项属于「自行判断」级别，请确认你确实不再需要它们。" : "") +
                         "\r\n\r\n删除后不经过回收站，无法恢复。确认清理吗？";
            if (MessageBox.Show(this, msg, "确认清理", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

            SetBusy(true, "正在清理…");
            ShowCategory(null);
            catList.Items.Clear();
            var list = junk;
            CancellationToken tk = cts.Token;
            JunkResult r = await Task.Run(() => JunkScanner.Clean(list, Report, tk));
            junk = junk.Where(c => c.Items.Count > 0).ToList();
            RebuildJunk();
            SetBusy(false, "清理完成，释放 " + Util.FormatSize(r.Freed) + "。");
            string text = "清理完成！\r\n\r\n已删除 " + r.Deleted + " 项，释放磁盘空间 " + Util.FormatSize(r.Freed) + "。";
            if (r.Failed.Count > 0)
                text += "\r\n\r\n有 " + r.Failed.Count + " 个文件/文件夹正在被使用或受系统保护，已跳过（留在列表中）。";
            MessageBox.Show(this, text, "清理结果", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // =====================================================================
        //  空间分析页
        // =====================================================================

        void BuildDisk()
        {
            pageDisk.Dock = DockStyle.Fill;
            pageDisk.BackColor = Theme.Bg;
            var head = new Panel { Dock = DockStyle.Top, Height = S(134), BackColor = Theme.Bg, Width = S(1000) };
            head.Controls.Add(Lbl("空间分析", Theme.Title, Theme.Text, S(24), S(18)));
            diskSub.ForeColor = Theme.Sub;
            diskSub.BackColor = Theme.Bg;
            diskSub.AutoEllipsis = true;
            diskSub.SetBounds(S(28), S(56), head.Width - S(56), S(22));
            diskSub.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            diskSub.Text = "看清磁盘被什么占用：文件类型占比、各文件夹大小、文件新旧分布、最大的文件。分析过程只读，不会改动任何文件。";
            head.Controls.Add(diskSub);

            int x = S(28), y = S(88);
            var bGo = Action("开始分析", "", 1, delegate { AnalyzeDisk(); }, "遍历所选磁盘并统计（大磁盘可能需要一两分钟）");
            bGo.Location = new Point(x, y);
            head.Controls.Add(bGo);
            x += bGo.Width + S(20);
            var dl = Lbl("选择磁盘", Theme.Ui, Theme.Sub, x, y + S(8));
            head.Controls.Add(dl);
            x += dl.PreferredWidth + S(10);
            foreach (string d in Util.FixedDrives())
            {
                var c = new Chip(d.TrimEnd('\\'));
                c.Tag = d;
                c.Radio = true;
                c.Checked = diskDrives.Count == 0;
                c.Location = new Point(x, y + S(3));
                c.Changed += delegate(object o, EventArgs e) { foreach (Chip k in diskDrives) if (k != o) k.Checked = false; };
                x += c.Width + S(6);
                diskDrives.Add(c);
                head.Controls.Add(c);
            }

            string[] modes = { "文件夹占用", "最大的文件", "文件类型排行" };
            x = head.Width - S(20);
            for (int i = modes.Length - 1; i >= 0; i--)
            {
                var c = new Chip(modes[i]);
                c.Radio = true;
                c.Checked = i == 0;
                c.Tag = i;
                x -= c.Width;
                c.Location = new Point(x, y + S(3));
                c.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                c.Changed += delegate(object o, EventArgs e)
                {
                    foreach (Chip k in diskModes) if (k != o) k.Checked = false;
                    ShowDiskMode((int)((Chip)o).Tag);
                };
                diskModes.Add(c);
                head.Controls.Add(c);
                x -= S(6);
            }

            diskView.Dock = DockStyle.Left;
            diskView.Width = S(452);

            diskList.Dock = DockStyle.Fill;
            diskList.VirtualMode = true;
            diskList.EmptyText = "分析完成后，这里会列出占用空间最多的文件夹、文件和文件类型";
            diskList.RetrieveVirtualItem += DiskItem;
            diskList.PillColor = (li, col) => li.Tag is int && ((diskMode == 1 && col == 3) || (diskMode == 2 && col == 1)) ? FileCats.Colors[(int)li.Tag] : Color.Empty;
            diskList.DoubleClick += delegate
            {
                if (disk == null || diskList.SelectedIndices.Count == 0) return;
                int i = diskList.SelectedIndices[0];
                if (diskMode == 0 && i < disk.Folders.Count) Reveal(disk.Folders[i].Path);
                else if (diskMode == 1 && i < disk.Big.Count) Reveal(disk.Big[i].Path);
            };
            diskList.SelectedIndexChanged += delegate
            {
                if (isBusy || disk == null || diskList.SelectedIndices.Count == 0) return;
                int i = diskList.SelectedIndices[0];
                if (diskMode == 0 && i < disk.Folders.Count) status.Text = disk.Folders[i].Path + "   ·   双击在资源管理器中打开";
                else if (diskMode == 1 && i < disk.Big.Count) status.Text = disk.Big[i].Path + "   ·   双击在资源管理器中定位";
            };
            ShowDiskMode(0);

            var wrap = new Panel { Dock = DockStyle.Fill, Padding = new Padding(S(24), 0, S(14), S(6)), BackColor = Theme.Bg };
            wrap.Controls.Add(diskList);
            wrap.Controls.Add(new Panel { Dock = DockStyle.Left, Width = S(14), BackColor = Theme.Bg });
            wrap.Controls.Add(diskView);
            pageDisk.Controls.Add(wrap);
            pageDisk.Controls.Add(head);
        }

        void DiskItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            int i = e.ItemIndex;
            string[] cells = { "", "", "", "" };
            object tag = null;
            if (disk != null)
            {
                if (diskMode == 0 && i < disk.Folders.Count)
                {
                    var f = disk.Folders[i];
                    string rel = f.Path.Length > disk.Root.Length ? f.Path.Substring(disk.Root.Length) : f.Path;
                    cells = new[] { (f.Depth >= 2 ? "      " : "") + rel, Util.FormatSize(f.Size), (100.0 * f.Size / Math.Max(1, disk.Used)).ToString("0.0") + "%", f.Files.ToString("N0") };
                }
                else if (diskMode == 1 && i < disk.Big.Count)
                {
                    var f = disk.Big[i];
                    cells = new[] { f.Path, Util.FormatSize(f.Size), f.Modified.ToString("yyyy-MM-dd"), FileCats.Names[f.Cat] };
                    tag = f.Cat;
                }
                else if (diskMode == 2 && i < disk.ExtList.Count)
                {
                    var x = disk.ExtList[i];
                    cells = new[] { x.Ext, FileCats.Names[x.Cat], Util.FormatSize(x.Size), x.Count.ToString("N0") };
                    tag = x.Cat;
                }
            }
            e.Item = new ListViewItem(cells);
            e.Item.Tag = tag;
        }

        void ShowDiskMode(int mode)
        {
            diskMode = mode;
            try { diskList.SelectedIndices.Clear(); diskList.VirtualListSize = 0; } catch { }
            diskList.FillColumn = -1;
            diskList.Columns.Clear();
            if (mode == 0)
            {
                diskList.Columns.Add("文件夹（前两级，按大小排序）", S(300));
                diskList.Columns.Add("大小", S(100), HorizontalAlignment.Right);
                diskList.Columns.Add("占已用", S(84), HorizontalAlignment.Right);
                diskList.Columns.Add("文件数", S(96), HorizontalAlignment.Right);
            }
            else if (mode == 1)
            {
                diskList.Columns.Add("文件（≥ 100 MB，前 300 个）", S(300));
                diskList.Columns.Add("大小", S(100), HorizontalAlignment.Right);
                diskList.Columns.Add("修改时间", S(104), HorizontalAlignment.Right);
                diskList.Columns.Add("类型", S(140));
            }
            else
            {
                diskList.Columns.Add("扩展名", S(200));
                diskList.Columns.Add("所属类别", S(150));
                diskList.Columns.Add("总大小", S(100), HorizontalAlignment.Right);
                diskList.Columns.Add("文件数", S(96), HorizontalAlignment.Right);
            }
            diskList.FillColumn = 0;
            int n = disk == null ? 0 : mode == 0 ? disk.Folders.Count : mode == 1 ? disk.Big.Count : disk.ExtList.Count;
            try { diskList.VirtualListSize = n; } catch { }
            diskList.Fit();
            diskList.Invalidate();
        }

        async void AnalyzeDisk()
        {
            Chip sel = diskDrives.FirstOrDefault(c => c.Checked);
            if (sel == null) return;
            string root = (string)sel.Tag;
            SetBusy(true, "正在分析 " + root + " …");
            var sw = Stopwatch.StartNew();
            DiskStats st;
            try
            {
                CancellationToken tk = cts.Token;
                st = await Task.Run(() => DiskAnalyzer.Analyze(root, Report, tk));
            }
            catch (OperationCanceledException) { SetBusy(false, "已取消分析。"); return; }
            catch (Exception ex) { SetBusy(false, "分析出错: " + ex.Message); return; }
            disk = st;
            diskView.Stats = st;
            ShowDiskMode(diskMode);

            // 生成几条具体结论
            var tips2 = new List<string>();
            int top = Enumerable.Range(0, 12).OrderByDescending(i => st.CatSize[i]).First();
            if (st.CatSize[top] > 0)
                tips2.Add("占用最多的是「" + FileCats.Names[top] + "」" + Util.FormatSize(st.CatSize[top]) + "（" + (100.0 * st.CatSize[top] / Math.Max(1, st.Used)).ToString("0") + "%）");
            long old = st.AgeSize[3] + st.AgeSize[4];
            if (old > 0) tips2.Add("超过 1 年没改动的文件共 " + Util.FormatSize(old));
            var f1 = st.Folders.FirstOrDefault(f => f.Depth == 1);
            if (f1 != null) tips2.Add("最大的文件夹是 " + f1.Path + "（" + Util.FormatSize(f1.Size) + "）");
            if (st.Big.Count > 0) tips2.Add("100 MB 以上的大文件有 " + st.Big.Count + (st.Big.Count >= 300 ? "+" : "") + " 个");
            diskSub.Text = string.Join("；", tips2) + "。";
            tips.SetToolTip(diskSub, diskSub.Text);
            SetBusy(false, "分析完成，用时 " + (int)sw.Elapsed.TotalSeconds + " 秒，共 " + st.Files.ToString("N0") + " 个文件。右上角可切换：文件夹占用 / 最大的文件 / 文件类型排行。");
        }

        // =====================================================================
        //  开发用：自动截图
        // =====================================================================

        static void Shot(Form f, string file)
        {
            Rectangle r = f.Bounds;
            using (var bmp = new Bitmap(r.Width, r.Height))
            {
                using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        async void RunShots()
        {
            TopMost = true;
            await Task.Delay(500);
            while (isBusy) await Task.Delay(300);
            await Task.Delay(5000);
            sortCol = 6; sortAsc = true; lv.SortColumn = 6; SortApps(); FillList();
            for (int i = 1; i < 4 && i < lv.Items.Count; i++) lv.Items[i].Selected = true;
            await Task.Delay(600);
            Shot(this, Path.Combine(ShotDir, "apps.png"));

            side.Selected = 1;
            ShowPage(1);
            foreach (Chip c in driveChips) c.Checked = ((string)c.Tag).StartsWith("D");
            ScanJunk();
            await Task.Delay(500);
            while (isBusy) await Task.Delay(300);
            await Task.Delay(700);
            Shot(this, Path.Combine(ShotDir, "junk.png"));

            side.Selected = 2;
            ShowPage(2);
            foreach (Chip c in diskDrives) c.Checked = ((string)c.Tag).StartsWith("D");
            AnalyzeDisk();
            await Task.Delay(500);
            while (isBusy) await Task.Delay(300);
            await Task.Delay(700);
            Shot(this, Path.Combine(ShotDir, "disk.png"));
            Close();
        }
    }
}
