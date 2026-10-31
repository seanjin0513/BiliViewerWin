// 生成 NSIS MUI 界面用的两张位图（24 位 BMP，BMP3 头、不压缩 —— NSIS 不接受 PNG）：
//   welcome-finish.bmp  164x314  完成页左侧竖幅：粉蓝对角渐变 + 白色电视机标志
//   header.bmp          150x57   页眉横条：横向粉蓝渐变 + 左侧白色电视机，右侧留白
//
// 电视机造型完全沿用 AppIcon（tools/MakeIcon/Program.cs，已在 macOS/Windows 两版核对过）：
//   tvH = tvW * 0.40/0.56，机身圆角 0.18*tvH，屏幕内缩 0.09*tvH、圆角 0.12*tvH，
//   两只脚宽 0.0625*tvW、高 0.1339*tvW（脚顶比机底高 0.0134*tvW，和图标里一样略微交叠），
//   天线线宽 0.05*tvW、从机顶两侧斜向外上方伸出 0.1786*tvW，
//   屏幕内三条白色弹幕条（高 0.11*屏幕高、圆角一半，宽度 0.42/0.30/0.50 倍屏幕宽，
//   x 从屏幕左 10% 起，竖直位置 0.32/0.51/0.74 倍屏幕高 —— 由图标的 CG 坐标换算而来）。
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;

namespace MakeIcon
{
    internal static class Artwork
    {
        // NSIS MUI 的接口图尺寸（固定值，MUI_WELCOMEFINISHPAGE_BITMAP / MUI_HEADERIMAGE_BITMAP）
        private const int BannerW = 164;
        private const int BannerH = 314;
        private const int HeaderW = 150;
        private const int HeaderH = 57;

        // 先按 4 倍绘制再缩小，弧线与渐变才不会有锯齿（BMP 只有 24 位，没有 alpha 可以依赖）
        private const int Supersample = 4;

        private static readonly Color Pink = Color.FromArgb(255, 251, 114, 153);   // #FB7299
        private static readonly Color Blue = Color.FromArgb(255, 35, 173, 229);    // #23ADE5

        public static void Run(string outDir)
        {
            if (string.IsNullOrWhiteSpace(outDir))
            {
                outDir = Path.Combine("installer", "assets");
            }
            outDir = Path.GetFullPath(outDir);
            Directory.CreateDirectory(outDir);

            string bannerPath = Path.Combine(outDir, "welcome-finish.bmp");
            string headerPath = Path.Combine(outDir, "header.bmp");

            // 幂等：同样的输入重画结果字节完全一致，内容没变就不重写（不白白改 mtime）
            WriteIfChanged(bannerPath, RenderBmp(BannerW, BannerH, DrawBanner));
            WriteIfChanged(headerPath, RenderBmp(HeaderW, HeaderH, DrawHeader));

            Report(bannerPath, BannerW, BannerH);
            Report(headerPath, HeaderW, HeaderH);
        }

        private static void WriteIfChanged(string path, byte[] bytes)
        {
            if (File.Exists(path))
            {
                byte[] old = File.ReadAllBytes(path);
                if (old.Length == bytes.Length)
                {
                    bool same = true;
                    for (int i = 0; i < old.Length; i++)
                    {
                        if (old[i] != bytes[i])
                        {
                            same = false;
                            break;
                        }
                    }
                    if (same)
                    {
                        return;   // 与磁盘上完全一致，跳过写入
                    }
                }
            }
            File.WriteAllBytes(path, bytes);
        }

        // ------------------------------------------------------------------
        // 两张图的内容
        // ------------------------------------------------------------------

        /// <summary>完成页竖幅：整幅对角渐变（左上粉 → 右下蓝），白色电视机居中。</summary>
        private static void DrawBanner(Graphics g, float w, float h)
        {
            FillDiagonalGradient(g, w, h);

            // 竖幅较窄：电视机宽度 = 画布宽 - 2*12px（四周留 12px 左右），整块 logo 垂直居中
            float tvW = w - 24f;
            float logoH = tvW * LogoHeightRatio();      // 含天线 + 支撑脚的总高
            float tvLeft = (w - tvW) / 2f;
            float tvTop = (h - logoH) / 2f + AntennaRise(tvW);   // 让“整块 logo”垂直居中而不是机身居中
            DrawTv(g, tvLeft, tvTop, tvW);
        }

        /// <summary>页眉横条：横向渐变（左粉 → 右蓝），白色电视机靠左，右侧留白。</summary>
        private static void DrawHeader(Graphics g, float w, float h)
        {
            FillHorizontalGradient(g, w, h);

            // 横条很矮，按高度定尺寸：机身 + 天线 + 脚刚好占满可用高度
            float tvW = h * 0.70f;
            float logoH = tvW * LogoHeightRatio();
            float tvLeft = w * 0.09f;
            float tvTop = (h - logoH) / 2f + AntennaRise(tvW);
            DrawTv(g, tvLeft, tvTop, tvW);
        }

        private static void FillDiagonalGradient(Graphics g, float w, float h)
        {
            using (var brush = new LinearGradientBrush(new PointF(0f, 0f), new PointF(w, h), Pink, Blue))
            {
                brush.WrapMode = WrapMode.Tile;
                // 长渐变在 sRGB 里直接插值中段会发灰，开 GammaCorrection 保持通透
                brush.GammaCorrection = true;
                g.FillRectangle(brush, 0f, 0f, w, h);
            }
        }

        private static void FillHorizontalGradient(Graphics g, float w, float h)
        {
            using (var brush = new LinearGradientBrush(new PointF(0f, 0f), new PointF(w, 0f), Pink, Blue))
            {
                brush.WrapMode = WrapMode.Tile;
                // 长渐变在 sRGB 里直接插值中段会发灰，开 GammaCorrection 保持通透
                brush.GammaCorrection = true;
                g.FillRectangle(brush, 0f, 0f, w, h);
            }
        }

        // ------------------------------------------------------------------
        // 电视机标志（System.Drawing 坐标：y 向下，原点左上）
        // ------------------------------------------------------------------

        /// <summary>logo 总高 / tvW：0.1786(天线) + 0.7143(机身) + 0.1205(脚出机身部分)。</summary>
        private static float LogoHeightRatio()
        {
            return AntennaRise(1f) + 0.40f / 0.56f + (FootHeight(1f) - FootOverlap(1f));
        }

        private static float AntennaRise(float tvW) { return tvW * 0.1786f; }

        private static float FootHeight(float tvW) { return tvW * 0.1339f; }

        private static float FootOverlap(float tvW) { return tvW * 0.0134f; }

        private static void DrawTv(Graphics g, float tvLeft, float tvTop, float tvW)
        {
            float tvH = tvW * 0.40f / 0.56f;            // 0.7142857 * tvW
            float tvBottom = tvTop + tvH;
            float inset = tvH * 0.09f;
            float footW = tvW * 0.0625f;
            float footH = FootHeight(tvW);

            using (var white = new SolidBrush(Color.White))
            using (var pink = new SolidBrush(Pink))
            {
                // 白色机身
                using (GraphicsPath tv = RoundedRect(new RectangleF(tvLeft, tvTop, tvW, tvH), tvH * 0.18f))
                {
                    g.FillPath(white, tv);
                }

                // 屏幕（B 站粉）
                using (GraphicsPath screen = RoundedRect(
                           new RectangleF(tvLeft + inset, tvTop + inset, tvW - inset * 2f, tvH - inset * 2f),
                           tvH * 0.12f))
                {
                    g.FillPath(pink, screen);
                }

                // 两只支撑脚（先画，让机身压住脚的顶端 0.0134*tvW，和图标的交叠关系一致）
                float footTop = tvBottom - FootOverlap(tvW);
                g.FillRectangle(white, tvLeft + tvW * 0.12f, footTop, footW, footH);
                g.FillRectangle(white, tvLeft + tvW * 0.88f - footW, footTop, footW, footH);

                // 两根天线（圆头端帽）
                using (var antenna = new Pen(Color.White, tvW * 0.05f))
                {
                    antenna.StartCap = LineCap.Round;
                    antenna.EndCap = LineCap.Round;
                    g.DrawLine(antenna,
                               tvLeft + tvW * 0.3571f, tvTop,
                               tvLeft + tvW * 0.1429f, tvTop - AntennaRise(tvW));
                    g.DrawLine(antenna,
                               tvLeft + tvW * 0.6429f, tvTop,
                               tvLeft + tvW * 0.8571f, tvTop - AntennaRise(tvW));
                }

                // 屏幕内的三条弹幕条
                float sw = tvW - inset * 2f;
                float sh = tvH - inset * 2f;
                float barH = sh * 0.11f;
                float barX = tvLeft + inset + sw * 0.10f;
                float screenTop = tvTop + inset;
                Danmu(g, white, barX, screenTop + sh * 0.32f, sw * 0.42f, barH);
                Danmu(g, white, barX, screenTop + sh * 0.51f, sw * 0.30f, barH);
                Danmu(g, white, barX, screenTop + sh * 0.74f, sw * 0.50f, barH);
            }
        }

        private static void Danmu(Graphics g, Brush brush, float x, float y, float w, float h)
        {
            using (GraphicsPath bar = RoundedRect(new RectangleF(x, y, w, h), h / 2f))
            {
                g.FillPath(brush, bar);
            }
        }

        private static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = radius * 2f;
            float limit = Math.Min(r.Width, r.Height);
            if (d > limit)
            {
                d = limit;
            }
            if (d <= 0f)
            {
                path.AddRectangle(r);
                return path;
            }

            path.AddArc(r.X, r.Y, d, d, 180f, 90f);
            path.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            path.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            path.CloseFigure();
            return path;
        }

        // ------------------------------------------------------------------
        // 渲染 + 手写 24 位 BMP（NSIS 只认 BMP；GDI+ 的 ImageFormat.Bmp 会带多余的调色板/头，手写更稳）
        // ------------------------------------------------------------------

        private static byte[] RenderBmp(int width, int height, Action<Graphics, float, float> paint)
        {
            float bigW = width * (float)Supersample;
            float bigH = height * (float)Supersample;

            using (var big = new Bitmap((int)bigW, (int)bigH, PixelFormat.Format32bppArgb))
            using (var small = new Bitmap(width, height, PixelFormat.Format24bppRgb))
            {
                big.SetResolution(96f, 96f);
                small.SetResolution(96f, 96f);

                using (Graphics g = Graphics.FromImage(big))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.CompositingMode = CompositingMode.SourceOver;
                    g.Clear(Color.White);
                    paint(g, bigW, bigH);
                }

                using (Graphics g = Graphics.FromImage(small))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(big, new Rectangle(0, 0, width, height),
                                 new Rectangle(0, 0, big.Width, big.Height), GraphicsUnit.Pixel);
                }

                return ToBmp24(small);
            }
        }

        /// <summary>BITMAPFILEHEADER(14) + BITMAPINFOHEADER(40) + 逐行 BGR（自下而上，4 字节对齐）。</summary>
        private static byte[] ToBmp24(Bitmap bmp)
        {
            int w = bmp.Width;
            int h = bmp.Height;
            int stride = ((w * 3) + 3) / 4 * 4;

            BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            byte[] pixels;
            try
            {
                pixels = new byte[stride * h];
                for (int y = 0; y < h; y++)
                {
                    // 位图里第 0 行是图像顶部；BMP 要自下而上，所以写到倒数第 y 行
                    IntPtr src = IntPtr.Add(data.Scan0, y * data.Stride);
                    System.Runtime.InteropServices.Marshal.Copy(src, pixels, (h - 1 - y) * stride, stride);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            int imageSize = stride * h;
            int fileSize = 14 + 40 + imageSize;
            var bytes = new byte[fileSize];
            using (var ms = new MemoryStream(bytes))
            using (var bw = new BinaryWriter(ms))
            {
                // BITMAPFILEHEADER
                bw.Write((byte)'B');
                bw.Write((byte)'M');
                bw.Write(fileSize);          // bfSize
                bw.Write((ushort)0);         // bfReserved1
                bw.Write((ushort)0);         // bfReserved2
                bw.Write(54);                // bfOffBits
                // BITMAPINFOHEADER（40 字节，不压缩，无调色板）
                bw.Write(40);                // biSize
                bw.Write(w);                 // biWidth
                bw.Write(h);                 // biHeight（正数 = 自下而上）
                bw.Write((ushort)1);         // biPlanes
                bw.Write((ushort)24);        // biBitCount
                bw.Write(0);                 // biCompression = BI_RGB
                bw.Write(imageSize);         // biSizeImage
                bw.Write(2835);              // biXPelsPerMeter（72 dpi）
                bw.Write(2835);              // biYPelsPerMeter
                bw.Write(0);                 // biClrUsed
                bw.Write(0);                 // biClrImportant
                bw.Write(pixels);
            }
            return bytes;
        }

        // ------------------------------------------------------------------
        // 打印结果 + 回读校验（保证 NSIS 能认）
        // ------------------------------------------------------------------
        private static void Report(string path, int width, int height)
        {
            byte[] b = File.ReadAllBytes(path);
            string sig = ((char)b[0]).ToString() + (char)b[1];
            int offBits = BitConverter.ToInt32(b, 10);
            int biSize = BitConverter.ToInt32(b, 14);
            int biW = BitConverter.ToInt32(b, 18);
            int biH = BitConverter.ToInt32(b, 22);
            ushort bpp = BitConverter.ToUInt16(b, 28);
            int compression = BitConverter.ToInt32(b, 30);

            bool ok = sig == "BM" && biSize == 40 && biW == width && biH == height &&
                      bpp == 24 && compression == 0 && offBits == 54;

            Console.WriteLine("已生成: " + path);
            Console.WriteLine("  " + b.Length.ToString(CultureInfo.InvariantCulture) + " 字节, " +
                              biW + "x" + biH + ", " + bpp + " 位, 压缩=" + compression +
                              ", 数据偏移=" + offBits + (ok ? "  [校验通过]" : "  [校验失败!]"));
            if (!ok)
            {
                throw new InvalidOperationException("BMP 头不符合 NSIS 要求: " + path);
            }
        }
    }
}
