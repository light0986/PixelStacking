using System;
using PixelStacking.Render;

namespace PixelStacking.Data
{
    /// <summary>膨脹的截面形狀：離輪廓邊緣越遠越厚，厚度怎麼隨距離增加。</summary>
    public enum InflateProfile
    {
        /// <summary>圓潤（橢圓截面：邊緣很薄、往中間很快變厚，像氣球）。</summary>
        Round,
        /// <summary>平滑（S 形：邊緣與中心都比較平緩）。</summary>
        Smooth,
        /// <summary>錐形（厚度跟距離成正比，像屋頂）。</summary>
        Cone,
        /// <summary>平板（每個有顏色的格子一樣厚，等於把圖片直接擠出來）。</summary>
        Flat,
    }

    /// <summary>膨脹往哪一邊鼓起。</summary>
    public enum InflateSide
    {
        /// <summary>前後對稱（以模型中央為對稱面，兩邊一樣鼓）。</summary>
        Both,
        /// <summary>只往觀看者那一側鼓起，背面是平的。</summary>
        Front,
        /// <summary>只往遠離觀看者的那一側鼓起，正面是平的。</summary>
        Back,
    }

    public sealed class InflateOptions
    {
        /// <summary>圖片當成哪一個視角看到的樣子（厚度往那個視角的切片方向長）。</summary>
        public ViewDirection View = ViewDirection.Front;
        /// <summary>圖片尺寸跟畫布不同時怎麼放。</summary>
        public ImageFit Fit = ImageFit.Contain;
        /// <summary>alpha 小於這個值的像素當成透明（0~255）。</summary>
        public int AlphaThreshold = 128;
        public InflateProfile Profile = InflateProfile.Round;
        public InflateSide Side = InflateSide.Both;
        /// <summary>true = 最厚處自動取「最大內切圓直徑」（圓盤會變成球），並且不超過模型厚度。</summary>
        public bool AutoDepth = true;
        /// <summary>最厚幾格（AutoDepth 為 false 時使用）。</summary>
        public int MaxDepth = 12;
        /// <summary>最薄幾格（輪廓邊緣的厚度，至少 1）。</summary>
        public int MinDepth = 2;
        /// <summary>要離邊緣幾格才長到最厚（0 = 自動：用整個形狀最深的那一點）。</summary>
        public double Radius = 0;
        /// <summary>厚度場平滑幾次（0~6），讓階梯變平順。</summary>
        public int Smooth = 1;
        /// <summary>套用前先把這一幀（這一層）清空。</summary>
        public bool Clear = true;

        public InflateOptions Clone() { return (InflateOptions)MemberwiseClone(); }
    }

    /// <summary>
    /// 2D 圖片「膨脹」成 3D：把圖片的輪廓（alpha）當剪影，算每個有顏色的格子離輪廓邊緣多遠（歐氏距離轉換），
    /// 距離越遠越厚，再沿視角的切片方向把那一格的顏色拉成一條柱子。純邏輯，不碰畫面。
    /// </summary>
    public static class Inflate
    {
        /// <summary>把圖片依 Fit 放進 cw × ch 的畫布，alpha 低於門檻的像素清成完全透明。</summary>
        public static int[] PrepareCanvas(LoadedImage im, int cw, int ch, ImageFit fit, int alphaThreshold)
        {
            int[] canvas = ImageImport.Fit(im.Pixels, im.Width, im.Height, cw, ch, fit);
            for (int i = 0; i < canvas.Length; i++)
            {
                if (((uint)canvas[i] >> 24) < (uint)Math.Max(1, alphaThreshold)) { canvas[i] = 0; }
            }
            return canvas;
        }

        /// <summary>
        /// 每個有顏色格子到最近「沒顏色格子」的歐氏距離（格子中心到格子中心）。畫布邊界外一律算沒顏色。
        /// 沒顏色的格子是 0；有顏色的格子最小是 1。
        /// </summary>
        public static double[] DistanceField(bool[] solid, int w, int h)
        {
            int pw = w + 2, ph = h + 2;   // 外面圍一圈「沒顏色」
            const double Inf = 1e20;
            double[] f = new double[pw * ph];
            for (int y = 0; y < ph; y++)
            {
                for (int x = 0; x < pw; x++)
                {
                    bool s = x > 0 && y > 0 && x <= w && y <= h && solid[(y - 1) * w + (x - 1)];
                    f[y * pw + x] = s ? Inf : 0;
                }
            }

            // Felzenszwalb：先對每一欄、再對每一列做一維平方距離轉換
            double[] col = new double[Math.Max(pw, ph)], outv = new double[Math.Max(pw, ph)];
            int[] v = new int[Math.Max(pw, ph)]; double[] z = new double[Math.Max(pw, ph) + 1];
            for (int x = 0; x < pw; x++)
            {
                for (int y = 0; y < ph; y++) { col[y] = f[y * pw + x]; }
                Dt1D(col, ph, outv, v, z);
                for (int y = 0; y < ph; y++) { f[y * pw + x] = outv[y]; }
            }
            for (int y = 0; y < ph; y++)
            {
                for (int x = 0; x < pw; x++) { col[x] = f[y * pw + x]; }
                Dt1D(col, pw, outv, v, z);
                for (int x = 0; x < pw; x++) { f[y * pw + x] = outv[x]; }
            }

            double[] result = new double[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++) { result[y * w + x] = solid[y * w + x] ? Math.Sqrt(f[(y + 1) * pw + (x + 1)]) : 0; }
            }
            return result;
        }

        private static void Dt1D(double[] f, int n, double[] d, int[] v, double[] z)
        {
            int k = 0;
            v[0] = 0; z[0] = double.NegativeInfinity; z[1] = double.PositiveInfinity;
            for (int q = 1; q < n; q++)
            {
                double s = ((f[q] + (double)q * q) - (f[v[k]] + (double)v[k] * v[k])) / (2.0 * q - 2.0 * v[k]);
                while (s <= z[k])
                {
                    k--;
                    s = ((f[q] + (double)q * q) - (f[v[k]] + (double)v[k] * v[k])) / (2.0 * q - 2.0 * v[k]);
                }
                k++;
                v[k] = q; z[k] = s; z[k + 1] = double.PositiveInfinity;
            }
            k = 0;
            for (int q = 0; q < n; q++)
            {
                while (z[k + 1] < q) { k++; }
                double dq = q - v[k];
                d[q] = dq * dq + f[v[k]];
            }
        }
        private static double ProfileOf(InflateProfile p, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            switch (p)
            {
                case InflateProfile.Round: return Math.Sqrt(1 - (1 - t) * (1 - t));
                case InflateProfile.Smooth: return t * t * (3 - 2 * t);
                case InflateProfile.Cone: return t;
                default: return 1;
            }
        }

        /// <summary>
        /// 每個畫布格要拉幾層（0 = 沒顏色）。indices 非 0 的格子才算有顏色（顏色對應到調色盤之後才定，所以量化掉的半透明也一致）。
        /// </summary>
        public static int[] Thickness(byte[] indices, int cw, int ch, int depthCount, InflateOptions o)
        {
            bool[] solid = new bool[indices.Length];
            for (int i = 0; i < solid.Length; i++) { solid[i] = indices[i] != 0; }

            double[] dist = DistanceField(solid, cw, ch);
            double dmax = 0;
            foreach (double d in dist) { if (d > dmax) { dmax = d; } }
            int[] result = new int[indices.Length];
            if (dmax <= 0) { return result; }

            double maxDepth = o.AutoDepth ? Math.Min(depthCount, Math.Max(1.0, 2 * (dmax - 0.5))) : Math.Min(depthCount, Math.Max(1, o.MaxDepth));
            double minDepth = Math.Min(maxDepth, Math.Max(1, o.MinDepth));
            double radius = o.Radius > 0 ? o.Radius : dmax;
            double span = Math.Max(0.5, radius - 0.5);

            double[] h = new double[indices.Length];
            for (int i = 0; i < h.Length; i++)
            {
                if (!solid[i]) { continue; }
                double t = (dist[i] - 0.5) / span;
                h[i] = minDepth + (maxDepth - minDepth) * ProfileOf(o.Profile, t);
            }

            // 平滑：只在有顏色的格子之間取 3×3 平均（輪廓外不參與，所以邊緣不會被拉薄）
            for (int it = 0; it < Math.Max(0, o.Smooth); it++)
            {
                double[] next = new double[h.Length];
                for (int y = 0; y < ch; y++)
                {
                    for (int x = 0; x < cw; x++)
                    {
                        int i = y * cw + x;
                        if (!solid[i]) { continue; }

                        double sum = 0; int n = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int xx = x + dx, yy = y + dy;
                                if (xx < 0 || yy < 0 || xx >= cw || yy >= ch || !solid[yy * cw + xx]) { continue; }
                                sum += h[yy * cw + xx]; n++;
                            }
                        }
                        next[i] = sum / n;
                    }
                }
                h = next;
            }

            for (int i = 0; i < h.Length; i++)
            {
                if (solid[i]) { result[i] = Math.Max(1, Math.Min(depthCount, (int)Math.Round(h[i]))); }
            }
            return result;
        }

        /// <summary>
        /// 把畫布上的顏色索引依厚度拉成柱子寫進模型（Both＝以中央對稱；Front＝從最後面往觀看者鼓；Back＝從最前面往遠處鼓）。
        /// clear 為 true 時先把整個模型（體素與部件標記）清空。回傳寫了幾個體素。
        /// </summary>
        public static int Fill(VoxelModel m, ViewDirection view, byte[] indices, int[] thickness, InflateSide side, bool clear)
        {
            int w = m.Width, h = m.Height, d = m.Depth;
            int cw = ViewMapper.CanvasWidth(view, w, h, d), ch = ViewMapper.CanvasHeight(view, w, h, d);
            int count = ViewMapper.SliceCount(view, w, h, d);
            if (indices.Length != cw * ch || thickness.Length != cw * ch) { throw new ArgumentException(Loc.Get(TextKey.Inflate_TheCanvasSizesDoNot)); }

            if (clear)
            {
                Array.Clear(m.Data, 0, m.Data.Length);
                if (m.Parts != null) { Array.Clear(m.Parts, 0, m.Parts.Length); }
            }

            int[] origin = new int[count], stepX = new int[count], stepY = new int[count];
            for (int s = 0; s < count; s++) { ViewMapper.Plane(view, s, w, h, d, out origin[s], out stepX[s], out stepY[s]); }

            int written = 0;
            for (int cy = 0; cy < ch; cy++)
            {
                for (int cx = 0; cx < cw; cx++)
                {
                    int i = cy * cw + cx;
                    int n = thickness[i];
                    if (n <= 0 || indices[i] == 0) { continue; }

                    int s0 = side == InflateSide.Both ? (count - n) / 2 : side == InflateSide.Front ? 0 : count - n;
                    for (int s = s0; s < s0 + n; s++)
                    {
                        int idx = origin[s] + cx * stepX[s] + cy * stepY[s];
                        m.Data[idx] = indices[i];
                        if (m.Parts != null) { m.Parts[idx] = 0; }
                        written++;
                    }
                }
            }
            return written;
        }
    }
}
