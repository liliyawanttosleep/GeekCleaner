using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace GeekCleaner
{
    // 用 Logo.Draw 的矢量绘制代码生成多尺寸 app.ico（以及一张预览图）
    static class IconGen
    {
        static Bitmap Render(int s)
        {
            var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp)) Logo.Draw(g, 0, 0, s);
            return bmp;
        }

        static byte[] Dib(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height, mask = ((w + 31) / 32) * 4 * h;
            var ms = new MemoryStream();
            var bw = new BinaryWriter(ms);
            bw.Write(40); bw.Write(w); bw.Write(h * 2); bw.Write((short)1); bw.Write((short)32);
            bw.Write(0); bw.Write(w * h * 4 + mask); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
            for (int y = h - 1; y >= 0; y--)
                for (int x = 0; x < w; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    bw.Write(c.B); bw.Write(c.G); bw.Write(c.R); bw.Write(c.A);
                }
            bw.Write(new byte[mask]);
            return ms.ToArray();
        }

        static void Main(string[] args)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
            var data = new List<byte[]>();
            foreach (int s in sizes)
            {
                using (Bitmap b = Render(s))
                {
                    if (s == 256)
                    {
                        var ms = new MemoryStream();
                        b.Save(ms, ImageFormat.Png);
                        data.Add(ms.ToArray());
                    }
                    else data.Add(Dib(b));
                }
            }
            using (var fs = File.Create(args[0]))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write((short)0); bw.Write((short)1); bw.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    bw.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); bw.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                    bw.Write((byte)0); bw.Write((byte)0); bw.Write((short)1); bw.Write((short)32);
                    bw.Write(data[i].Length); bw.Write(offset);
                    offset += data[i].Length;
                }
                foreach (byte[] d in data) bw.Write(d);
            }
            if (args.Length > 1)
            {
                // 预览：大图 + 各小尺寸，分别放在深色和浅色背景上
                using (var pv = new Bitmap(720, 300))
                using (Graphics g = Graphics.FromImage(pv))
                {
                    g.Clear(Color.FromArgb(30, 32, 40));
                    g.FillRectangle(Brushes.WhiteSmoke, 360, 0, 360, 300);
                    int[] show = { 256, 64, 48, 32, 24, 16 };
                    for (int side = 0; side < 2; side++)
                    {
                        int ox = side * 360;
                        using (Bitmap b = Render(200)) g.DrawImage(b, ox + 14, 50);
                        int y = 20;
                        for (int i = 1; i < show.Length; i++)
                        {
                            using (Bitmap b = Render(show[i])) g.DrawImage(b, ox + 250, y);
                            y += show[i] + 16;
                        }
                    }
                    pv.Save(args[1], ImageFormat.Png);
                }
            }
        }
    }
}
