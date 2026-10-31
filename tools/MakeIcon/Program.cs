// 生成 BiliViewer 应用图标：圆角矩形粉蓝渐变背景 + 白色电视机轮廓 + 弹幕线条
// 本文件是 macOS 版 scripts/make_icon.swift（CoreGraphics）的逐行移植版：
//   * 绘制坐标沿用 Swift 的 CoreGraphics 习惯（y 轴向上、原点在左下），
//     统一通过 CgRect()/CgPoint() 翻转到 System.Drawing 的（y 轴向下、原点左上）。
//   * 1024x1024 逻辑画布绘制一次，再高质量缩放到 16/24/32/48/64/128/256 打包成多尺寸 .ico。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;

namespace MakeIcon
{
    internal static class Program
    {
        // 逻辑画布尺寸（与 Swift 版一致）
        private const int Canvas = 1024;
        private static readonly float S = Canvas;

        // 输出的 .ico 包含的尺寸
        private static readonly int[] IconSizes = { 16, 24, 32, 48, 64, 128, 256 };

        private static int Main(string[] args)
        {
            try
            {
                Run(args);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("生成图标失败: " + ex.Message);
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
        }

        /// <summary>
        /// 用法：
        ///   dotnet run --project tools\MakeIcon                      → Assets\AppIcon.ico
        ///   dotnet run --project tools\MakeIcon -- out.ico           → 指定 ico 路径
        ///   dotnet run --project tools\MakeIcon -- --installer-assets [目录]
        ///                                                            → installer\assets 下两张 NSIS 位图
        ///   dotnet run --project tools\MakeIcon -- --all             → 图标 + NSIS 位图（build.ps1 用这个）
        /// </summary>
        private static void Run(string[] args)
        {
            string first = (args != null && args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])) ? args[0] : null;

            if (string.Equals(first, "--installer-assets", StringComparison.OrdinalIgnoreCase))
            {
                string dir = (args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]))
                    ? args[1]
                    : Path.Combine("installer", "assets");
                Artwork.Run(dir);
                return;
            }

            if (string.Equals(first, "--all", StringComparison.OrdinalIgnoreCase))
            {
                WriteIcon(Path.Combine("Assets", "AppIcon.ico"));
                Artwork.Run(Path.Combine("installer", "assets"));
                return;
            }

            WriteIcon(first ?? Path.Combine("Assets", "AppIcon.ico"));
        }

        private static void WriteIcon(string outPath)
        {
            outPath = Path.GetFullPath(outPath);
            string outDir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
            }

            using (Bitmap master = RenderMaster())
            {
                var pngFrames = new List<KeyValuePair<int, byte[]>>();
                foreach (int px in IconSizes)
                {
                    using (Bitmap scaled = Downscale(master, px))
                    using (var ms = new MemoryStream())
                    {
                        scaled.Save(ms, ImageFormat.Png);
                        pngFrames.Add(new KeyValuePair<int, byte[]>(px, ms.ToArray()));
                    }
                }

                byte[] ico = BuildIco(pngFrames);
                File.WriteAllBytes(outPath, ico);   // 幂等覆盖

                Console.WriteLine("图标已生成: " + outPath);
                Console.WriteLine("总大小: " + ico.Length + " 字节 (" +
                                  (ico.Length / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " KB)");
                Console.WriteLine("包含 " + pngFrames.Count + " 个尺寸:");
                foreach (KeyValuePair<int, byte[]> kv in pngFrames)
                {
                    Console.WriteLine("  " + kv.Key.ToString().PadLeft(3) + "x" +
                                      kv.Key.ToString().PadRight(3) + " : " + kv.Value.Length + " 字节");
                }
            }
        }

        // ------------------------------------------------------------------
        // 坐标换算：Swift/CoreGraphics（y 向上，原点左下）→ System.Drawing（y 向下，原点左上）
        // ------------------------------------------------------------------

        /// <summary>CoreGraphics 矩形（x,y 为左下角）转成 System.Drawing 矩形（左上角）。</summary>
        private static RectangleF CgRect(float x, float y, float w, float h)
        {
            return new RectangleF(x, S - (y + h), w, h);
        }

        /// <summary>CoreGraphics 点转成 System.Drawing 点。</summary>
        private static PointF CgPoint(float x, float y)
        {
            return new PointF(x, S - y);
        }

        private static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            if (radius <= 0f)
            {
                path.AddRectangle(r);
                return path;
            }

            float d = radius * 2f;
            float limit = Math.Min(r.Width, r.Height);
            if (d > limit)
            {
                d = limit;
            }

            path.AddArc(r.X, r.Y, d, d, 180f, 90f);                    // 左上
            path.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);            // 右上
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);     // 右下
            path.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);            // 左下
            path.CloseFigure();
            return path;
        }

        /// <summary>按 Swift 版 make_icon.swift 的绘制顺序，画出 1024x1024 主位图。</summary>
        private static Bitmap RenderMaster()
        {
            var bmp = new Bitmap(Canvas, Canvas, PixelFormat.Format32bppArgb);
            bmp.SetResolution(96f, 96f);

            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.CompositingMode = CompositingMode.SourceOver;
                g.Clear(Color.Transparent);

                // ---- 背景：圆角矩形，B 站粉蓝对角渐变（左上 #FB7299 → 右下 #23ADE5） ----
                float radius = S * 0.225f;   // macOS 图标标准圆角比例
                using (GraphicsPath bg = RoundedRect(new RectangleF(0f, 0f, S, S), radius))
                {
                    g.SetClip(bg);
                    using (var brush = new LinearGradientBrush(
                               new PointF(0f, 0f),          // Swift 的 (0, s) = 左上
                               new PointF(S, S),            // Swift 的 (s, 0) = 右下
                               Color.FromArgb(255, 251, 114, 153),
                               Color.FromArgb(255, 35, 173, 229)))
                    {
                        brush.WrapMode = WrapMode.Tile;
                        g.FillRectangle(brush, 0f, 0f, S, S);
                    }
                    g.ResetClip();
                }

                // ---- 电视机（居中）：屏幕圆角矩形 + 底部两个支撑脚 ----
                float tvW = S * 0.56f, tvH = S * 0.40f;
                float tvX = (S - tvW) / 2f, tvY = S * 0.30f;   // tvY 为 CG 坐标下的底边
                float inset = tvH * 0.09f;
                float sx = tvX + inset, sy = tvY + inset;
                float sw = tvW - inset * 2f, sh = tvH - inset * 2f;
                float footW = S * 0.035f, footH = S * 0.075f;

                using (var white = new SolidBrush(Color.White))
                using (var pink = new SolidBrush(Color.FromArgb(255, 251, 114, 153)))
                using (GraphicsPath tv = RoundedRect(CgRect(tvX, tvY, tvW, tvH), tvH * 0.18f))
                {
                    // 白色电视外壳
                    g.FillPath(white, tv);

                    // ---- 屏幕内部（B 站粉色） ----
                    using (GraphicsPath screen = RoundedRect(CgRect(sx, sy, sw, sh), tvH * 0.12f))
                    {
                        g.FillPath(pink, screen);
                    }

                    // ---- 支撑脚（电视机下方左右各一） ----
                    g.FillRectangle(white, CgRect(tvX + tvW * 0.12f, tvY - footH * 0.9f, footW, footH));
                    g.FillRectangle(white, CgRect(tvX + tvW * 0.88f - footW, tvY - footH * 0.9f, footW, footH));

                    // ---- 天线：两条斜线从机顶伸出（圆头端帽） ----
                    using (var antenna = new Pen(Color.White, S * 0.028f))
                    {
                        antenna.StartCap = LineCap.Round;
                        antenna.EndCap = LineCap.Round;
                        g.DrawLine(antenna, CgPoint(S * 0.42f, tvY + tvH), CgPoint(S * 0.30f, S * 0.80f));
                        g.DrawLine(antenna, CgPoint(S * 0.58f, tvY + tvH), CgPoint(S * 0.70f, S * 0.80f));
                    }

                    // ---- 弹幕：屏幕内三条白色圆角短条 ----
                    float midY = sy + sh / 2f;
                    float barH = sh * 0.11f;
                    Danmu(g, white, sx + sw * 0.10f, midY - sh * 0.05f + sh * 0.12f, sw * 0.42f, barH);
                    Danmu(g, white, sx + sw * 0.10f, midY - sh * 0.12f, sw * 0.30f, barH);
                    Danmu(g, white, sx + sw * 0.10f, midY - sh * 0.35f, sw * 0.50f, barH);
                }
            }

            return bmp;
        }

        /// <summary>屏幕内的一条弹幕（参数使用 CoreGraphics 坐标：y 为底边）。</summary>
        private static void Danmu(Graphics g, Brush brush, float x, float y, float w, float h)
        {
            using (GraphicsPath bar = RoundedRect(CgRect(x, y, w, h), h / 2f))
            {
                g.FillPath(brush, bar);
            }
        }

        /// <summary>高质量缩放到目标像素尺寸（主图已在 1024 画布上抗锯齿绘制）。</summary>
        private static Bitmap Downscale(Bitmap master, int px)
        {
            var scaled = new Bitmap(px, px, PixelFormat.Format32bppArgb);
            scaled.SetResolution(96f, 96f);
            using (Graphics g = Graphics.FromImage(scaled))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.Clear(Color.Transparent);
                g.DrawImage(master, new Rectangle(0, 0, px, px),
                            new Rectangle(0, 0, master.Width, master.Height), GraphicsUnit.Pixel);
            }
            return scaled;
        }

        // ------------------------------------------------------------------
        // .ico 容器：手写 ICONDIR + ICONDIRENTRY + PNG 数据
        // （不用 Icon.Save(Bitmap)：那样会丢尺寸/位深）
        // ------------------------------------------------------------------
        private static byte[] BuildIco(List<KeyValuePair<int, byte[]>> frames)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                // ICONDIR: reserved=0, type=1(icon), count=N
                w.Write((ushort)0);
                w.Write((ushort)1);
                w.Write((ushort)frames.Count);

                int offset = 6 + 16 * frames.Count;
                foreach (KeyValuePair<int, byte[]> frame in frames)
                {
                    byte dim = frame.Key >= 256 ? (byte)0 : (byte)frame.Key;   // 256 用 0 表示
                    w.Write(dim);                       // bWidth
                    w.Write(dim);                       // bHeight
                    w.Write((byte)0);                   // bColorCount（>=256 色写 0）
                    w.Write((byte)0);                   // bReserved
                    w.Write((ushort)1);                 // wPlanes
                    w.Write((ushort)32);                // wBitCount
                    w.Write((uint)frame.Value.Length);  // dwBytesInRes
                    w.Write((uint)offset);              // dwImageOffset
                    offset += frame.Value.Length;
                }

                foreach (KeyValuePair<int, byte[]> frame in frames)
                {
                    w.Write(frame.Value);
                }

                w.Flush();
                return ms.ToArray();
            }
        }
    }
}
