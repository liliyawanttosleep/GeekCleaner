using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GeekCleaner
{
    /// <summary>配色、字体与绘图辅助。</summary>
    static class Theme
    {
        public static float Scale = 1f;

        public static readonly Color Bg = Color.FromArgb(16, 18, 25);
        public static readonly Color Side = Color.FromArgb(11, 13, 19);
        public static readonly Color Panel = Color.FromArgb(24, 27, 37);
        public static readonly Color PanelHi = Color.FromArgb(33, 37, 51);
        public static readonly Color Sel = Color.FromArgb(28, 46, 52);
        public static readonly Color Border = Color.FromArgb(44, 49, 66);
        public static readonly Color Text = Color.FromArgb(232, 236, 245);
        public static readonly Color Sub = Color.FromArgb(138, 147, 170);
        public static readonly Color Accent = Color.FromArgb(61, 245, 196);
        public static readonly Color Accent2 = Color.FromArgb(56, 189, 248);
        public static readonly Color Violet = Color.FromArgb(139, 124, 255);
        public static readonly Color Danger = Color.FromArgb(255, 107, 129);
        public static readonly Color Warn = Color.FromArgb(255, 190, 110);

        public static Font Ui, UiBold, Small, Title, Big, Glyph, GlyphBig;
        public static Icon AppIcon;

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string app, string id);
        [DllImport("uxtheme.dll", EntryPoint = "#135")]
        static extern int SetPreferredAppMode(int mode);
        [DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")]
        static extern int SetWindowLong(IntPtr hwnd, int index, int value);

        /// <summary>去掉窗口的横向滚动条样式（列始终撑满宽度，不需要横向滚动）。</summary>
        public static void NoHScroll(IntPtr hwnd)
        {
            int st = GetWindowLong(hwnd, -16);
            if ((st & 0x00100000) != 0) SetWindowLong(hwnd, -16, st & ~0x00100000);
        }

        [DllImport("user32.dll")]
        static extern bool GetClientRect(IntPtr hwnd, out RECT r);
        struct RECT { public int L, T, R, B; }

        /// <summary>实时的客户区宽度（WinForms 缓存的 ClientSize 在滚动条出现时不会及时更新）。</summary>
        public static int ClientWidth(Control c)
        {
            RECT r;
            return c.IsHandleCreated && GetClientRect(c.Handle, out r) ? r.R - r.L : c.ClientSize.Width;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wp, string lp);

        public static int S(int v) { return (int)Math.Round(v * Scale); }

        public static void Init()
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) Scale = g.DpiX / 96f;
            const string f = "Microsoft YaHei UI";
            Ui = new Font(f, 9f);
            UiBold = new Font(f, 9f, FontStyle.Bold);
            Small = new Font(f, 8f);
            Title = new Font(f, 17f, FontStyle.Bold);
            Big = new Font("Segoe UI", 24f, FontStyle.Bold);
            string gf = "Segoe Fluent Icons";
            using (var t = new Font(gf, 10f)) if (t.Name != gf) gf = "Segoe MDL2 Assets";
            Glyph = new Font(gf, 10.5f);
            GlyphBig = new Font(gf, 12.5f);
            try { SetPreferredAppMode(2); } catch { }
            try
            {
                using (Stream s = typeof(Theme).Assembly.GetManifestResourceStream("GeekCleaner.app.ico"))
                    if (s != null) AppIcon = new Icon(s);
            }
            catch { }
        }

        public static void DarkTitle(Form f)
        {
            try { int v = 1; DwmSetWindowAttribute(f.Handle, 20, ref v, 4); } catch { }
        }

        public static void DarkScroll(Control c)
        {
            try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { }
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            float d = rad * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            var p = new GraphicsPath();
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Color c, RectangleF r, float rad)
        {
            using (var p = Round(r, rad))
            using (var b = new SolidBrush(c)) g.FillPath(b, p);
        }

        public static void DrawRound(Graphics g, Color c, RectangleF r, float rad)
        {
            using (var p = Round(r, rad))
            using (var pen = new Pen(c, 1f)) g.DrawPath(pen, p);
        }

        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        /// <summary>state: 0 未选, 1 已选, 2 部分选中</summary>
        public static void DrawCheck(Graphics g, Rectangle r, int state)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF rf = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1);
            if (state == 0) DrawRound(g, Color.FromArgb(96, 104, 128), rf, r.Width * 0.25f);
            else
            {
                FillRound(g, Accent, rf, r.Width * 0.25f);
                using (var pen = new Pen(Color.FromArgb(6, 40, 32), Math.Max(1.6f, r.Width * 0.13f)))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    if (state == 1)
                        g.DrawLines(pen, new[] {
                            new PointF(r.X + r.Width * 0.24f, r.Y + r.Height * 0.52f),
                            new PointF(r.X + r.Width * 0.43f, r.Y + r.Height * 0.70f),
                            new PointF(r.X + r.Width * 0.77f, r.Y + r.Height * 0.32f) });
                    else
                        g.DrawLine(pen, r.X + r.Width * 0.28f, r.Y + r.Height * 0.5f, r.X + r.Width * 0.72f, r.Y + r.Height * 0.5f);
                }
            }
            g.SmoothingMode = old;
        }

        static readonly Dictionary<string, Bitmap> letters = new Dictionary<string, Bitmap>();
        static readonly Color[] palette = { Accent, Accent2, Violet, Danger, Warn, Color.FromArgb(244, 114, 182) };

        /// <summary>没有图标的程序：用首字母生成一个彩色占位图标。</summary>
        public static Image LetterIcon(string name)
        {
            string ch = string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant();
            int ci = (ch[0] * 7 + (name ?? "").Length) % palette.Length;
            string key = ch + ci;
            Bitmap bmp;
            if (letters.TryGetValue(key, out bmp)) return bmp;
            int s = S(22);
            bmp = new Bitmap(s, s);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                Color c = palette[ci];
                FillRound(g, Color.FromArgb(50, c), new RectangleF(0, 0, s, s), s * 0.28f);
                using (var b = new SolidBrush(c))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(ch, UiBold, b, new RectangleF(0, 1, s, s), sf);
            }
            letters[key] = bmp;
            return bmp;
        }
    }

    /// <summary>
    /// 程序标志：一个渐变的 “G” 环，开口处逐渐碎裂成像素方块飞散 —— 寓意把程序“分解、抹除”。
    /// 图标文件和界面里的标志共用这段矢量绘制代码。
    /// </summary>
    static class Logo
    {
        public static void Draw(Graphics g, float x, float y, float s)
        {
            GraphicsState st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            bool tiny = s < 28;

            RectangleF r = new RectangleF(x + s * 0.03f, y + s * 0.03f, s * 0.94f, s * 0.94f);
            using (GraphicsPath path = Theme.Round(r, s * 0.23f))
            {
                using (var b = new LinearGradientBrush(r, Color.FromArgb(38, 42, 84), Color.FromArgb(9, 11, 20), 60f))
                    g.FillPath(b, path);
                g.SetClip(path);
                Glow(g, x - s * 0.30f, y + s * 0.38f, s * 0.95f, Color.FromArgb(105, 61, 245, 196));
                Glow(g, x + s * 0.45f, y - s * 0.30f, s * 0.85f, Color.FromArgb(120, 139, 124, 255));
                g.ResetClip();
                using (var p = new Pen(Color.FromArgb(46, 255, 255, 255), Math.Max(1f, s * 0.008f))) g.DrawPath(p, path);
            }

            float cx = x + s * (tiny ? 0.50f : 0.455f), cy = y + s * (tiny ? 0.50f : 0.555f);
            float R = s * (tiny ? 0.265f : 0.235f), w = s * (tiny ? 0.15f : 0.108f);
            RectangleF ring = new RectangleF(cx - R, cy - R, 2 * R, 2 * R);
            RectangleF gb = RectangleF.Inflate(ring, w, w);
            using (var lb = new LinearGradientBrush(gb, Color.White, Color.White, -45f))
            {
                var cb = new ColorBlend();
                cb.Colors = new[] { Color.FromArgb(61, 245, 196), Color.FromArgb(56, 189, 248), Color.FromArgb(139, 124, 255) };
                cb.Positions = new[] { 0f, 0.55f, 1f };
                lb.InterpolationColors = cb;
                using (var pen = new Pen(lb, w))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = tiny ? LineCap.Round : LineCap.Flat;
                    g.DrawArc(pen, ring, 0, tiny ? 296 : 297);
                    pen.EndCap = LineCap.Round;
                    g.DrawLine(pen, cx + R, cy, cx + R * 0.10f, cy);
                }
            }

            if (!tiny)
            {
                // 碎裂飞散的像素块：位置 x,y  大小  不透明度  旋转角
                float[][] ps = {
                    new[] { 0.680f, 0.350f, 0.108f, 255f, 14f },
                    new[] { 0.790f, 0.282f, 0.082f, 250f, 28f },
                    new[] { 0.712f, 0.198f, 0.062f, 235f, -12f },
                    new[] { 0.862f, 0.196f, 0.054f, 215f, 40f },
                    new[] { 0.790f, 0.122f, 0.040f, 190f, 20f },
                    new[] { 0.896f, 0.340f, 0.038f, 190f, -25f },
                    new[] { 0.900f, 0.118f, 0.028f, 160f, 10f }
                };
                Color c0 = Color.FromArgb(130, 160, 255), c1 = Color.FromArgb(250, 120, 190);
                for (int i = 0; i < ps.Length; i++)
                {
                    if (s < 40 && i > 2) break;
                    float[] p = ps[i];
                    float sz = p[2] * s;
                    Color c = Theme.Mix(c0, c1, i / (float)(ps.Length - 1));
                    GraphicsState s2 = g.Save();
                    g.TranslateTransform(x + p[0] * s, y + p[1] * s);
                    g.RotateTransform(p[4]);
                    Theme.FillRound(g, Color.FromArgb((int)p[3], c), new RectangleF(-sz / 2, -sz / 2, sz, sz), sz * 0.24f);
                    g.Restore(s2);
                }
            }
            g.Restore(st);
        }

        static void Glow(Graphics g, float x, float y, float d, Color c)
        {
            using (var gp = new GraphicsPath())
            {
                gp.AddEllipse(x, y, d, d);
                using (var pb = new PathGradientBrush(gp))
                {
                    pb.CenterColor = c;
                    pb.SurroundColors = new[] { Color.FromArgb(0, c) };
                    g.FillPath(pb, gp);
                }
            }
        }
    }

    // ======================================================================
    //  控件
    // ======================================================================

    class Flat : Control
    {
        public Flat()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            Font = Theme.Ui;
            TabStop = false;
        }

        protected Color ParentBg { get { return Parent != null ? Parent.BackColor : Theme.Bg; } }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    }

    /// <summary>圆角按钮。Kind: 0 普通, 1 主要(强调色), 2 危险, 3 仅图标</summary>
    class Btn : Flat
    {
        public int Kind;
        public string Glyph;
        bool hot, down;

        public Btn(string text, string glyph, int kind)
        {
            Text = text;
            Glyph = glyph;
            Kind = kind;
            Cursor = Cursors.Hand;
            int w = Theme.S(15) * 2;
            Font tf = kind == 1 ? Theme.UiBold : Theme.Ui;
            if (text.Length > 0) w += TextRenderer.MeasureText(text, tf, Size.Empty, TextFormatFlags.NoPadding).Width;
            if (glyph != null) w += Theme.S(18) + (text.Length > 0 ? Theme.S(5) : 0);
            Size = new Size(text.Length == 0 ? Theme.S(34) : w, Theme.S(34));
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(ParentBg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            float rad = Theme.S(8);
            Color fg;
            if (Kind == 1)
            {
                Color bg = down ? Theme.Mix(Theme.Accent, Color.Black, 0.18f) : hot ? Theme.Mix(Theme.Accent, Color.White, 0.25f) : Theme.Accent;
                if (!Enabled) bg = Theme.Mix(Theme.Accent, Theme.Bg, 0.65f);
                Theme.FillRound(g, bg, r, rad);
                fg = Color.FromArgb(5, 36, 29);
            }
            else if (Kind == 2)
            {
                if (hot) Theme.FillRound(g, Color.FromArgb(down ? 70 : 40, Theme.Danger), r, rad);
                Theme.DrawRound(g, Color.FromArgb(Enabled ? 190 : 70, Theme.Danger), r, rad);
                fg = Enabled ? Theme.Danger : Theme.Mix(Theme.Danger, Theme.Bg, 0.6f);
            }
            else
            {
                if (Kind == 0 || hot) Theme.FillRound(g, down ? Theme.Border : hot ? Theme.PanelHi : Theme.Panel, r, rad);
                if (Kind == 0) Theme.DrawRound(g, Theme.Border, r, rad);
                fg = Enabled ? Theme.Text : Theme.Sub;
            }
            Font tf = Kind == 1 ? Theme.UiBold : Theme.Ui;
            const TextFormatFlags fl = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            int x = Text.Length == 0 ? (Width - Theme.S(18)) / 2 : Theme.S(15);
            if (Glyph != null)
            {
                TextRenderer.DrawText(g, Glyph, Theme.Glyph, new Rectangle(x, 1, Theme.S(18), Height), fg, fl | TextFormatFlags.HorizontalCenter);
                x += Theme.S(18) + Theme.S(5);
            }
            if (Text.Length > 0)
                TextRenderer.DrawText(g, Text, tf, new Rectangle(x, 0, Width - x, Height - 1), fg, fl);
        }
    }

    /// <summary>可切换的胶囊标签（筛选、磁盘选择）。</summary>
    class Chip : Flat
    {
        bool chk, hot;
        public event EventHandler Changed;
        public bool Radio; // 单选组：点击已选中的不会取消

        public Chip(string text)
        {
            Text = text;
            Cursor = Cursors.Hand;
            Size = new Size(TextRenderer.MeasureText(text, Theme.Ui, Size.Empty, TextFormatFlags.NoPadding).Width + Theme.S(28), Theme.S(28));
        }

        public bool Checked
        {
            get { return chk; }
            set { chk = value; Invalidate(); }
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnClick(EventArgs e)
        {
            if (Radio && chk) return;
            chk = !chk;
            Invalidate();
            if (Changed != null) Changed(this, EventArgs.Empty);
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(ParentBg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            if (chk) Theme.FillRound(g, Color.FromArgb(38, Theme.Accent), r, Height / 2f);
            else if (hot) Theme.FillRound(g, Theme.Panel, r, Height / 2f);
            Theme.DrawRound(g, chk ? Color.FromArgb(170, Theme.Accent) : Theme.Border, r, Height / 2f);
            TextRenderer.DrawText(g, Text, Theme.Ui, new Rectangle(0, 0, Width, Height - 1), chk ? Theme.Accent : Theme.Sub,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    class SearchBox : Flat
    {
        public readonly TextBox Box = new TextBox();

        public SearchBox(string cue)
        {
            Size = new Size(Theme.S(250), Theme.S(34));
            Box.BorderStyle = BorderStyle.None;
            Box.BackColor = Theme.Panel;
            Box.ForeColor = Theme.Text;
            Box.Font = Theme.Ui;
            Box.GotFocus += delegate { Invalidate(); };
            Box.LostFocus += delegate { Invalidate(); };
            Box.HandleCreated += delegate { Theme.SendMessage(Box.Handle, 0x1501, IntPtr.Zero, cue); };
            Controls.Add(Box);
            Cursor = Cursors.IBeam;
        }

        protected override void OnClick(EventArgs e) { Box.Focus(); base.OnClick(e); }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Box.SetBounds(Theme.S(36), (Height - Box.Height) / 2, Width - Theme.S(48), Box.Height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(ParentBg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Theme.FillRound(g, Theme.Panel, r, Theme.S(8));
            Theme.DrawRound(g, Box.Focused ? Color.FromArgb(170, Theme.Accent) : Theme.Border, r, Theme.S(8));
            TextRenderer.DrawText(g, "", Theme.Glyph, new Rectangle(Theme.S(10), 1, Theme.S(22), Height), Theme.Sub,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        }
    }

    /// <summary>忙碌时在状态栏顶部流动的细线。</summary>
    class BusyBar : Flat
    {
        readonly Timer timer = new Timer();
        float pos;
        bool active;

        public BusyBar()
        {
            Height = Theme.S(2);
            timer.Interval = 25;
            timer.Tick += delegate { pos += 0.014f; if (pos > 1.3f) pos = -0.3f; Invalidate(); };
        }

        public bool Active
        {
            get { return active; }
            set { active = value; pos = -0.3f; timer.Enabled = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Border);
            if (!active) return;
            int w = (int)(Width * 0.3f), x = (int)(Width * pos);
            Rectangle r = new Rectangle(x, 0, Math.Max(1, w), Height);
            using (var b = new LinearGradientBrush(r, Theme.Accent2, Theme.Accent, 0f)) e.Graphics.FillRectangle(b, r);
        }
    }

    /// <summary>左侧导航栏：标志、页面切换、磁盘空间。</summary>
    class Sidebar : Flat
    {
        public string[] Items = new string[0], Glyphs = new string[0];
        public event EventHandler SelectedChanged;
        int sel, hot = -1;
        readonly List<string[]> drives = new List<string[]>();
        readonly List<float> usage = new List<float>();

        public Sidebar()
        {
            BackColor = Theme.Side;
            Width = Theme.S(216);
            RefreshDrives();
        }

        public int Selected
        {
            get { return sel; }
            set { sel = value; Invalidate(); }
        }

        public void RefreshDrives()
        {
            drives.Clear();
            usage.Clear();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    drives.Add(new[] { d.Name.TrimEnd('\\'), Util.FormatSize(d.AvailableFreeSpace) + " 可用", "共 " + Util.FormatSize(d.TotalSize) });
                    usage.Add(1f - (float)d.AvailableFreeSpace / d.TotalSize);
                }
                catch { }
            }
            Invalidate();
        }

        Rectangle ItemRect(int i)
        {
            return new Rectangle(Theme.S(12), Theme.S(104) + i * Theme.S(48), Width - Theme.S(24), Theme.S(42));
        }

        int Hit(Point p)
        {
            for (int i = 0; i < Items.Length; i++) if (ItemRect(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = Hit(e.Location);
            if (h != hot) { hot = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hot = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int h = Hit(e.Location);
            if (h >= 0 && h != sel)
            {
                sel = h;
                Invalidate();
                if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty);
            }
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Side);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            const TextFormatFlags fl = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

            int ls = Theme.S(44);
            Logo.Draw(g, Theme.S(18), Theme.S(24), ls);
            TextRenderer.DrawText(g, "GeekCleaner", new Font("Segoe UI", 12.5f, FontStyle.Bold), new Rectangle(Theme.S(72), Theme.S(24), Width, Theme.S(26)), Theme.Text, fl);
            TextRenderer.DrawText(g, "卸载 · 清理 · 不留痕迹", Theme.Small, new Rectangle(Theme.S(73), Theme.S(49), Width, Theme.S(18)), Theme.Sub, fl);

            for (int i = 0; i < Items.Length; i++)
            {
                Rectangle r = ItemRect(i);
                bool s = i == sel;
                if (s || i == hot) Theme.FillRound(g, s ? Theme.PanelHi : Theme.Panel, r, Theme.S(9));
                if (s) Theme.FillRound(g, Theme.Accent, new RectangleF(r.X, r.Y + Theme.S(11), Theme.S(3), r.Height - Theme.S(22)), Theme.S(2));
                Color c = s ? Theme.Text : Theme.Sub;
                TextRenderer.DrawText(g, Glyphs[i], Theme.GlyphBig, new Rectangle(r.X + Theme.S(14), r.Y + 1, Theme.S(24), r.Height), s ? Theme.Accent : Theme.Sub, fl | TextFormatFlags.HorizontalCenter);
                TextRenderer.DrawText(g, Items[i], s ? Theme.UiBold : Theme.Ui, new Rectangle(r.X + Theme.S(48), r.Y, r.Width, r.Height - 1), c, fl);
            }

            // 磁盘空间
            int rowH = Theme.S(46);
            int y = Height - drives.Count * rowH - Theme.S(14);
            TextRenderer.DrawText(g, "磁盘空间", Theme.Small, new Rectangle(Theme.S(22), y - Theme.S(26), Width, Theme.S(18)), Theme.Sub, fl);
            for (int i = 0; i < drives.Count; i++)
            {
                int x = Theme.S(22), w = Width - Theme.S(44);
                TextRenderer.DrawText(g, drives[i][0], Theme.UiBold, new Rectangle(x, y, w, Theme.S(18)), Theme.Text, fl);
                TextRenderer.DrawText(g, drives[i][1], Theme.Small, new Rectangle(x, y, w, Theme.S(18)), Theme.Sub, fl | TextFormatFlags.Right);
                RectangleF bar = new RectangleF(x, y + Theme.S(24), w, Theme.S(5));
                Theme.FillRound(g, Theme.PanelHi, bar, bar.Height / 2);
                float u = Math.Max(0.02f, Math.Min(1f, usage[i]));
                RectangleF used = new RectangleF(bar.X, bar.Y, bar.Width * u, bar.Height);
                using (var p = Theme.Round(used, bar.Height / 2))
                using (var b = new LinearGradientBrush(new RectangleF(bar.X - 1, bar.Y, bar.Width + 2, bar.Height),
                        u > 0.9f ? Theme.Warn : Theme.Accent2, u > 0.9f ? Theme.Danger : Theme.Accent, 0f))
                    g.FillPath(b, p);
                y += rowH;
            }
            using (var pen = new Pen(Theme.Border)) g.DrawLine(pen, Width - 1, 0, Width - 1, Height);
        }
    }

    class DarkColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected { get { return Theme.PanelHi; } }
        public override Color MenuItemBorder { get { return Theme.PanelHi; } }
        public override Color MenuBorder { get { return Theme.Border; } }
        public override Color ToolStripDropDownBackground { get { return Theme.Panel; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Panel; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Panel; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Panel; } }
        public override Color SeparatorDark { get { return Theme.Border; } }
        public override Color SeparatorLight { get { return Theme.Border; } }
    }

    /// <summary>深色自绘列表：支持图标、复选框、彩色标签列、悬停高亮、自动撑满的列。</summary>
    class DarkList : ListView
    {
        public Func<ListViewItem, bool> CheckGetter;
        public Action<ListViewItem> CheckToggle;
        public Func<ListViewItem, Image> IconGetter;
        public Func<ListViewItem, int, Color> PillColor;
        public int FillColumn = -1, SortColumn = -1;
        public bool SortAsc = true;
        public string EmptyText;
        int hover = -1;
        bool fitting;

        public DarkList()
        {
            OwnerDraw = true;
            View = View.Details;
            FullRowSelect = true;
            HideSelection = false;
            BorderStyle = BorderStyle.None;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Ui;
            DoubleBuffered = true;
            var il = new ImageList();
            il.ImageSize = new Size(1, Theme.S(34));
            SmallImageList = il;
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.DarkScroll(this); }

        int Count { get { return VirtualMode ? VirtualListSize : Items.Count; } }

        public void Fit()
        {
            if (FillColumn < 0 || fitting || FillColumn >= Columns.Count) return;
            fitting = true;
            int w = Theme.ClientWidth(this); // 不含纵向滚动条；滚动条出现/消失时会重新计算
            lastCw = w;
            for (int i = 0; i < Columns.Count; i++) if (i != FillColumn) w -= Columns[i].Width;
            Columns[FillColumn].Width = Math.Max(Theme.S(180), w);
            fitting = false;
        }

        int lastCw = -1;
        protected override void OnResize(EventArgs e) { base.OnResize(e); Fit(); }
        protected override void OnClientSizeChanged(EventArgs e) { base.OnClientSizeChanged(e); Fit(); }
        protected override void OnColumnWidthChanged(ColumnWidthChangedEventArgs e) { base.OnColumnWidthChanged(e); Fit(); }

        void InvalidateRow(int i)
        {
            try { if (i >= 0 && i < Count) Invalidate(GetItemRect(i)); } catch { }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            ListViewItem it = GetItemAt(Theme.S(8), e.Y);
            int idx = it == null ? -1 : it.Index;
            if (idx != hover) { InvalidateRow(hover); hover = idx; InvalidateRow(hover); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); InvalidateRow(hover); hover = -1; }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (CheckToggle == null || e.Button != MouseButtons.Left || e.X > Theme.S(36)) return;
            ListViewItem it = GetItemAt(Theme.S(8), e.Y);
            if (it != null) { CheckToggle(it); InvalidateRow(it.Index); }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode != Keys.Space || CheckToggle == null) return;
            // 多选时统一设为同一状态（以第一行的相反状态为准）
            bool first = true, target = true;
            foreach (int i in SelectedIndices)
            {
                ListViewItem it = Items[i];
                if (first) { target = !CheckGetter(it); first = false; }
                if (CheckGetter(it) != target) CheckToggle(it);
                InvalidateRow(i);
            }
            e.Handled = true;
        }

        protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
        {
            Graphics g = e.Graphics;
            using (var b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, e.Bounds);
            using (var p = new Pen(Theme.Border)) g.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            string t = e.Header.Text + (e.ColumnIndex == SortColumn ? (SortAsc ? "  ▲" : "  ▼") : "");
            Rectangle r = Rectangle.Inflate(e.Bounds, -Theme.S(10), 0);
            TextFormatFlags fl = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            if (e.Header.TextAlign == HorizontalAlignment.Right) fl |= TextFormatFlags.Right;
            TextRenderer.DrawText(g, t, Theme.Small, r, Theme.Sub, fl);
        }

        protected override void OnDrawItem(DrawListViewItemEventArgs e) { }

        protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle r = e.Bounds;
            if (e.ColumnIndex == 0) r.Width = Columns[0].Width;
            bool sel = e.Item.Selected, hot = e.ItemIndex == hover;
            using (var b = new SolidBrush(sel ? Theme.Sel : hot ? Theme.Panel : Theme.Bg)) g.FillRectangle(b, r);
            using (var p = new Pen(Color.FromArgb(26, 29, 40))) g.DrawLine(p, r.Left, r.Bottom - 1, r.Right, r.Bottom - 1);

            int x = r.X + Theme.S(10);
            if (e.ColumnIndex == 0)
            {
                if (sel) using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, r.X, r.Y + Theme.S(7), Theme.S(3), r.Height - Theme.S(14));
                if (CheckGetter != null)
                {
                    int cs = Theme.S(16);
                    Theme.DrawCheck(g, new Rectangle(x, r.Y + (r.Height - cs) / 2, cs, cs), CheckGetter(e.Item) ? 1 : 0);
                    x += cs + Theme.S(10);
                }
                if (IconGetter != null)
                {
                    Image img = IconGetter(e.Item);
                    int s = Theme.S(22);
                    if (img != null)
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(img, new Rectangle(x, r.Y + (r.Height - s) / 2, s, s));
                    }
                    x += s + Theme.S(10);
                }
            }
            Rectangle tr = new Rectangle(x, r.Y, r.Right - x - Theme.S(8), r.Height - 1);
            if (tr.Width <= 0) return;
            string text = e.SubItem.Text;
            Color pill = PillColor != null ? PillColor(e.Item, e.ColumnIndex) : Color.Empty;
            if (!pill.IsEmpty && text.Length > 0)
            {
                int tw = TextRenderer.MeasureText(g, text, Theme.Small, Size.Empty, TextFormatFlags.NoPadding).Width + Theme.S(16);
                tw = Math.Min(tw, tr.Width);
                int ph = Theme.S(20);
                RectangleF pr = new RectangleF(x, r.Y + (r.Height - ph) / 2f, tw, ph);
                var old = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Theme.FillRound(g, Color.FromArgb(36, pill), pr, ph / 2f);
                g.SmoothingMode = old;
                TextRenderer.DrawText(g, text, Theme.Small, Rectangle.Round(pr), pill,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                return;
            }
            TextFormatFlags fl = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            if (Columns[e.ColumnIndex].TextAlign == HorizontalAlignment.Right) fl |= TextFormatFlags.Right;
            TextRenderer.DrawText(g, text, Theme.Ui, tr, e.ColumnIndex == 0 ? Theme.Text : Theme.Sub, fl);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0083 && FillColumn >= 0) Theme.NoHScroll(m.HWnd); // WM_NCCALCSIZE
            base.WndProc(ref m);
            // 滚动条在布局/绘制过程中才出现时，事后再校正一次
            if ((m.Msg == 0x000F || m.Msg == 0x0005 || m.Msg == 0x0047) && IsHandleCreated && Theme.ClientWidth(this) != lastCw)
                BeginInvoke((Action)Fit);
            if (m.Msg != 0x000F) return;
            // 用背景色盖住最后一行以下的区域（系统主题会在那里画列分隔线）
            int n = Count, y = Theme.S(28);
            try { if (n > 0) y = GetItemRect(n - 1).Bottom; } catch { return; }
            if (y >= ClientSize.Height) return;
            using (Graphics g = CreateGraphics())
            {
                using (var b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, 0, y, ClientSize.Width, ClientSize.Height - y);
                if (n == 0 && !string.IsNullOrEmpty(EmptyText))
                    TextRenderer.DrawText(g, EmptyText, Theme.Ui, new Rectangle(0, y, ClientSize.Width, ClientSize.Height - y),
                        Theme.Sub, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            }
        }
    }
}
