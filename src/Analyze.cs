using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GeekCleaner
{
    class FolderStat { public string Path; public long Size, Files; public int Depth; }
    class FileStat { public string Path; public long Size; public DateTime Modified; public int Cat; }
    class ExtStat { public string Ext; public int Cat; public long Size, Count; }

    /// <summary>文件类型分类。</summary>
    static class FileCats
    {
        public const int System = 10, Other = 11, Unread = 12;
        public static readonly string[] Names = {
            "视频", "图片", "音频", "文档", "压缩包", "安装包与镜像", "程序与库", "游戏与资源数据", "开发与代码", "虚拟磁盘",
            "Windows 系统文件", "其他", "未能读取 / 系统保留"
        };
        public static readonly Color[] Colors = {
            Color.FromArgb(139, 124, 255), Color.FromArgb(56, 189, 248), Color.FromArgb(244, 114, 182), Color.FromArgb(61, 245, 196),
            Color.FromArgb(255, 190, 110), Color.FromArgb(255, 138, 101), Color.FromArgb(96, 165, 250), Color.FromArgb(250, 204, 21),
            Color.FromArgb(52, 211, 153), Color.FromArgb(255, 107, 129), Color.FromArgb(120, 130, 160), Color.FromArgb(86, 94, 120),
            Color.FromArgb(58, 63, 82)
        };
        static readonly Dictionary<string, int> map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        static FileCats()
        {
            string[] sets = {
                "mp4 mkv avi mov wmv flv webm m4v ts rmvb rm mpg mpeg 3gp vob",
                "jpg jpeg png gif bmp webp tif tiff heic raw cr2 nef psd svg ico dds tga",
                "mp3 wav flac aac ogg m4a wma ape opus mid",
                "pdf doc docx xls xlsx ppt pptx txt md rtf csv epub mobi odt tex chm",
                "zip rar 7z tar gz bz2 xz zst cab tgz",
                "msi iso img msix appx msixbundle appxbundle msp apk",
                "exe dll sys ocx pyd so node drv mui cpl scr",
                "pak bin dat assets bundle vpk mix big wad cache db sqlite ldb bik unity3d upk arc forge rpf ress resource bnk wem ucas utoc pck",
                "py js ts cs cpp c h hpp java go rs html css json xml yaml yml ipynb pyc class jar whl lib obj pdb o a map",
                "vdi vhd vhdx vmdk qcow2 wim esd"
            };
            for (int i = 0; i < sets.Length; i++)
                foreach (string e in sets[i].Split(' ')) map[e] = i;
        }

        public static int Of(string ext)
        {
            int c;
            return map.TryGetValue(ext, out c) ? c : Other;
        }
    }

    class DiskStats
    {
        public string Root;
        public long Total, Free, Bytes, Files, Dirs;
        public long[] CatSize = new long[13], CatCount = new long[13];
        public long[] AgeSize = new long[5], AgeCount = new long[5];
        public Dictionary<string, ExtStat> Ext = new Dictionary<string, ExtStat>(StringComparer.OrdinalIgnoreCase);
        public List<FolderStat> Folders = new List<FolderStat>();
        public List<FileStat> Big = new List<FileStat>();
        public List<ExtStat> ExtList = new List<ExtStat>();
        public long Used { get { return Total - Free; } }

        public void Merge(DiskStats o)
        {
            Bytes += o.Bytes; Files += o.Files; Dirs += o.Dirs;
            for (int i = 0; i < CatSize.Length; i++) { CatSize[i] += o.CatSize[i]; CatCount[i] += o.CatCount[i]; }
            for (int i = 0; i < 5; i++) { AgeSize[i] += o.AgeSize[i]; AgeCount[i] += o.AgeCount[i]; }
            foreach (var kv in o.Ext)
            {
                ExtStat e;
                if (!Ext.TryGetValue(kv.Key, out e)) Ext[kv.Key] = kv.Value;
                else { e.Size += kv.Value.Size; e.Count += kv.Value.Count; }
            }
            Folders.AddRange(o.Folders);
            Big.AddRange(o.Big);
        }

        public void AddFile(FileInfo fi, long len, bool sys, DateTime now)
        {
            Bytes += len;
            Files++;
            string ext = fi.Extension;
            ext = ext.Length > 1 && ext.Length <= 12 ? ext.Substring(1).ToLowerInvariant() : "";
            int cat = sys ? FileCats.System : FileCats.Of(ext);
            CatSize[cat] += len;
            CatCount[cat]++;
            string key = ext.Length == 0 ? "(无扩展名)" : "." + ext;
            ExtStat e;
            if (!Ext.TryGetValue(key, out e)) Ext[key] = e = new ExtStat { Ext = key, Cat = sys ? FileCats.Of(ext) : cat };
            e.Size += len;
            e.Count++;
            DateTime m = fi.LastWriteTime;
            double days = (now - m).TotalDays;
            int b = days < 30 ? 0 : days < 180 ? 1 : days < 365 ? 2 : days < 365 * 3 ? 3 : 4;
            AgeSize[b] += len;
            AgeCount[b]++;
            if (len >= 100L * 1024 * 1024) Big.Add(new FileStat { Path = fi.FullName, Size = len, Modified = m, Cat = cat });
        }
    }

    /// <summary>遍历整个磁盘，统计文件类型、文件夹占用、文件新旧分布和最大文件。只读。</summary>
    static class DiskAnalyzer
    {
        class Ctx { public Action<string> Progress; public CancellationToken Ct; public int Counter; public DateTime Now; }

        public static DiskStats Analyze(string root, Action<string> progress, CancellationToken ct)
        {
            var ctx = new Ctx { Progress = progress, Ct = ct, Now = DateTime.Now };
            var st = new DiskStats { Root = root };
            var di = new DriveInfo(root);
            st.Total = di.TotalSize;
            st.Free = di.AvailableFreeSpace;
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

            // 第一、二级目录作为并行任务单元
            var tops = new List<FolderStat>();
            var work = new List<Tuple<string, int, bool>>(); // 路径, 所属一级目录下标, 是否系统目录
            foreach (var fsi in SafeEnum(root))
            {
                if ((fsi.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                var fi = fsi as FileInfo;
                if (fi != null) { st.AddFile(fi, fi.Length, (fi.Attributes & FileAttributes.System) != 0, ctx.Now); continue; }
                bool sys = Util.IsUnder(fsi.FullName, win);
                var top = new FolderStat { Path = fsi.FullName, Depth = 1 };
                tops.Add(top);
                st.Dirs++;
                foreach (var f2 in SafeEnum(fsi.FullName))
                {
                    if ((f2.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var fi2 = f2 as FileInfo;
                    if (fi2 != null) { st.AddFile(fi2, fi2.Length, sys, ctx.Now); top.Size += fi2.Length; top.Files++; }
                    else work.Add(Tuple.Create(f2.FullName, tops.Count - 1, sys));
                }
            }

            try
            {
                Parallel.ForEach(work, delegate(Tuple<string, int, bool> w)
                {
                    var local = new DiskStats();
                    long size, files;
                    Walk(w.Item1, 2, local, w.Item3, ctx, out size, out files);
                    lock (st)
                    {
                        st.Merge(local);
                        tops[w.Item2].Size += size;
                        tops[w.Item2].Files += files;
                    }
                });
            }
            catch (AggregateException ae)
            {
                if (ae.Flatten().InnerExceptions.Any(x => x is OperationCanceledException)) throw new OperationCanceledException();
                throw;
            }

            st.Folders.AddRange(tops);
            st.Folders = st.Folders.Where(f => f.Size > 0).OrderByDescending(f => f.Size).Take(400).ToList();
            st.Big = st.Big.OrderByDescending(f => f.Size).Take(300).ToList();
            st.ExtList = st.Ext.Values.OrderByDescending(e => e.Size).Take(300).ToList();
            long unread = st.Used - st.Bytes;
            if (unread > 0) st.CatSize[FileCats.Unread] = unread;
            return st;
        }

        static List<FileSystemInfo> SafeEnum(string dir)
        {
            var l = new List<FileSystemInfo>();
            try { foreach (var f in new DirectoryInfo(dir).EnumerateFileSystemInfos()) l.Add(f); } catch { }
            return l;
        }

        static void Walk(string dir, int depth, DiskStats acc, bool sys, Ctx ctx, out long size, out long files)
        {
            size = 0; files = 0;
            acc.Dirs++;
            ctx.Ct.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref ctx.Counter) % 400 == 0 && ctx.Progress != null) ctx.Progress("正在分析: " + dir);
            List<string> subs = null;
            try
            {
                foreach (var fsi in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    if ((fsi.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var fi = fsi as FileInfo;
                    if (fi == null) { if (subs == null) subs = new List<string>(); subs.Add(fsi.FullName); continue; }
                    long len = fi.Length;
                    size += len;
                    files++;
                    acc.AddFile(fi, len, sys, ctx.Now);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            if (subs != null)
                foreach (string s in subs)
                {
                    long ss, sf;
                    Walk(s, depth + 1, acc, sys, ctx, out ss, out sf);
                    size += ss;
                    files += sf;
                }
            if (depth <= 2) acc.Folders.Add(new FolderStat { Path = dir, Size = size, Files = files, Depth = depth });
        }
    }

    /// <summary>空间分析左侧面板：概况卡片 + 文件类型环形图 + 图例 + 文件新旧分布。</summary>
    class AnalysisView : Flat
    {
        DiskStats st;
        static readonly string[] AgeNames = { "30 天内", "1 – 6 个月", "6 – 12 个月", "1 – 3 年", "3 年以上" };
        static readonly Color[] AgeColors = { Theme.Accent, Theme.Accent2, Theme.Violet, Theme.Warn, Theme.Danger };

        public DiskStats Stats
        {
            get { return st; }
            set { st = value; Invalidate(); }
        }

        static int S(int v) { return Theme.S(v); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            const TextFormatFlags fl = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
            int w = Width - S(4);
            if (st == null)
            {
                Theme.FillRound(g, Theme.Panel, new RectangleF(0, 0, w, Height - S(6)), S(12));
                TextRenderer.DrawText(g, "选择一个磁盘，点击「开始分析」\r\n\r\n将统计文件类型占比、文件夹占用、\r\n文件新旧分布和最大的文件", Theme.Ui,
                    new Rectangle(0, 0, w, Height), Theme.Sub, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            // ---- 概况卡片 2 x 2 ----
            int gap = S(10), cw = (w - gap) / 2, ch = S(56);
            string[] labels = { "已用空间", "可用空间", "文件数量", "文件夹数量" };
            string[] values = {
                Util.FormatSize(st.Used) + "  /  " + Util.FormatSize(st.Total),
                Util.FormatSize(st.Free) + "  (" + (st.Total > 0 ? (100.0 * st.Free / st.Total).ToString("0") : "0") + "%)",
                st.Files.ToString("N0"), st.Dirs.ToString("N0")
            };
            using (var vf = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold))
                for (int i = 0; i < 4; i++)
                {
                    int x = (i % 2) * (cw + gap), y = (i / 2) * (ch + gap);
                    Theme.FillRound(g, Theme.Panel, new RectangleF(x, y, cw, ch), S(10));
                    TextRenderer.DrawText(g, labels[i], Theme.Small, new Rectangle(x + S(14), y + S(7), cw - S(20), S(18)), Theme.Sub, fl);
                    TextRenderer.DrawText(g, values[i], vf, new Rectangle(x + S(14), y + S(26), cw - S(20), S(24)), i == 1 ? Theme.Accent : Theme.Text, fl);
                }

            // ---- 环形图 ----
            int top = 2 * ch + gap + S(16);
            int d = S(184);
            Rectangle pr = new Rectangle(S(4), top, d, d);
            var order = Enumerable.Range(0, st.CatSize.Length).Where(i => st.CatSize[i] > 0).OrderByDescending(i => i == FileCats.Unread ? -1 : st.CatSize[i]).ToList();
            long total = Math.Max(1, order.Sum(i => st.CatSize[i]));
            float a = -90f;
            foreach (int i in order)
            {
                float sweep = 360f * st.CatSize[i] / total;
                if (sweep < 0.4f) { a += sweep; continue; }
                using (var b = new SolidBrush(FileCats.Colors[i])) g.FillPie(b, pr, a, sweep);
                a += sweep;
            }
            a = -90f;
            using (var pen = new Pen(Theme.Bg, Math.Max(1.5f, S(2))))
                foreach (int i in order)
                {
                    double rad = a * Math.PI / 180;
                    g.DrawLine(pen, pr.X + d / 2f, pr.Y + d / 2f, pr.X + d / 2f + (float)Math.Cos(rad) * d / 2f, pr.Y + d / 2f + (float)Math.Sin(rad) * d / 2f);
                    a += 360f * st.CatSize[i] / total;
                }
            int hole = (int)(d * 0.60f);
            using (var b = new SolidBrush(Theme.Bg)) g.FillEllipse(b, pr.X + (d - hole) / 2, pr.Y + (d - hole) / 2, hole, hole);
            using (var bf = new Font("Segoe UI", 13f, FontStyle.Bold))
                TextRenderer.DrawText(g, Util.FormatSize(st.Used), bf, new Rectangle(pr.X, pr.Y + d / 2 - S(24), d, S(28)), Theme.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, st.Root.TrimEnd('\\') + " 已用", Theme.Small, new Rectangle(pr.X, pr.Y + d / 2 + S(4), d, S(18)), Theme.Sub,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // ---- 图例 ----
            int lx = pr.Right + S(18), ly = top - S(2), rowH = S(19), lw = w - lx;
            int shown = 0;
            long rest = 0;
            foreach (int i in order)
            {
                if (shown >= 9 && order.Count > 10) { rest += st.CatSize[i]; continue; }
                LegendRow(g, lx, ly + shown * rowH, lw, rowH, FileCats.Colors[i], FileCats.Names[i], st.CatSize[i], total);
                shown++;
            }
            if (rest > 0) LegendRow(g, lx, ly + shown * rowH, lw, rowH, Theme.Border, "其余类型", rest, total);

            // ---- 文件新旧分布 ----
            int ay = top + d + S(18);
            TextRenderer.DrawText(g, "文件新旧分布（按最后修改时间；越旧越可能已不再使用）", Theme.Small, new Rectangle(S(2), ay, w, S(18)), Theme.Sub, fl);
            ay += S(24);
            long amax = Math.Max(1, st.AgeSize.Max());
            int lab = S(86), val = S(150), ah = S(24);
            for (int i = 0; i < 5; i++)
            {
                int y = ay + i * ah;
                TextRenderer.DrawText(g, AgeNames[i], Theme.Ui, new Rectangle(S(2), y, lab, ah - S(4)), Theme.Text, fl);
                RectangleF bar = new RectangleF(lab + S(6), y + S(6), w - lab - val - S(14), S(8));
                Theme.FillRound(g, Theme.Panel, bar, bar.Height / 2);
                float frac = (float)st.AgeSize[i] / amax;
                if (frac > 0) Theme.FillRound(g, AgeColors[i], new RectangleF(bar.X, bar.Y, Math.Max(bar.Height, bar.Width * frac), bar.Height), bar.Height / 2);
                string t = Util.FormatSize(st.AgeSize[i]) + "  ·  " + (100.0 * st.AgeSize[i] / Math.Max(1, st.Bytes)).ToString("0") + "%";
                TextRenderer.DrawText(g, t, Theme.Small, new Rectangle(w - val, y, val, ah - S(4)), Theme.Sub, fl | TextFormatFlags.Right);
            }
        }

        static void LegendRow(Graphics g, int x, int y, int w, int h, Color c, string name, long size, long total)
        {
            const TextFormatFlags fl = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
            int dot = S(9), vw = S(118);
            Theme.FillRound(g, c, new RectangleF(x, y + (h - dot) / 2f, dot, dot), S(3));
            TextRenderer.DrawText(g, name, Theme.Small, new Rectangle(x + dot + S(8), y, w - vw - dot - S(10), h), Theme.Text, fl);
            double pct = 100.0 * size / total;
            TextRenderer.DrawText(g, Util.FormatSize(size) + "  " + (pct < 0.1 ? "<0.1" : pct.ToString("0.0")) + "%", Theme.Small,
                new Rectangle(x + w - vw, y, vw, h), Theme.Sub, fl | TextFormatFlags.Right);
        }
    }
}
