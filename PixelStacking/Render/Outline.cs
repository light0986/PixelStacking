using System;
using System.Threading.Tasks;

namespace PixelStacking.Render
{
    /// <summary>
    /// 輪廓線設定（預覽與匯出共用）。線的顏色＝它所在位置底色加深（<see cref="Darkness"/>）。
    /// 兩種線：外輪廓（模型外圍多一圈）、面折線（模型內部：不同朝向的面交界、前面的東西壓在後面東西上的邊）。
    /// </summary>
    public sealed class OutlineOptions
    {
        /// <summary>外輪廓：模型外圍加一圈（會佔用模型外的 <see cref="Thickness"/> 個像素）。</summary>
        public bool Silhouette = true;

        /// <summary>面折線：不同朝向的面交界（頂面與側面…）、近的東西壓在遠的東西上的邊，畫在模型內部。只有實心體素預覽有；層疊模式沒有面的概念。</summary>
        public bool Creases = true;

        public const int MinThickness = 1, MaxThickness = 3;
        public const int MinDarkness = 20, MaxDarkness = 90;

        private int _thickness = 1, _darkness = 55;

        /// <summary>線寬（輸出像素；匯出時是匯出圖上的像素）。</summary>
        public int Thickness
        {
            get { return _thickness; }
            set { _thickness = Math.Max(MinThickness, Math.Min(MaxThickness, value)); }
        }

        /// <summary>線的亮度：底色 RGB 保留幾 %（越小越黑）。</summary>
        public int Darkness
        {
            get { return _darkness; }
            set { _darkness = Math.Max(MinDarkness, Math.Min(MaxDarkness, value)); }
        }

        public bool Any { get { return Silhouette || Creases; } }

        /// <summary>外輪廓需要在模型四周多留的空間（輸出像素）。</summary>
        public int Room { get { return Silhouette ? _thickness : 0; } }

        public OutlineOptions Clone()
        {
            return new OutlineOptions { Silhouette = Silhouette, Creases = Creases, Thickness = Thickness, Darkness = Darkness };
        }
    }

    /// <summary>
    /// 輪廓線後處理：在渲染完的畫面上掃一遍。外輪廓看「透明像素旁邊有沒有模型」，
    /// 面折線看每個像素記下的「朝向」與「深度」（實心體素渲染時順便記）。每個像素的新顏色只依渲染結果決定，可以平行處理。
    /// </summary>
    internal static class OutlinePass
    {
        /// <summary>兩個相鄰像素深度差超過這個（模型格數）才算「前後兩個東西」的邊。</summary>
        private const float DepthJump = 1.2f;

        /// <summary>朝向編號（1~6 = 軸 × 2 + 正負 + 1）對應的明暗係數：頂面最亮、底面最暗，折線畫在比較暗的那一邊。</summary>
        internal static int ShadeOf(int faceCode)
        {
            int axis = (faceCode - 1) >> 1, positive = (faceCode - 1) & 1;
            return axis == 2 ? (positive == 1 ? 256 : 140) : axis == 0 ? 214 : 176;
        }

        private static int Darken(int c, int percent)
        {
            int r = ((c >> 16) & 255) * percent / 100;
            int g = ((c >> 8) & 255) * percent / 100;
            int b = (c & 255) * percent / 100;
            return (c & unchecked((int)0xFF000000)) | (r << 16) | (g << 8) | b;
        }

        /// <summary>
        /// 對 buf（w × h，非預乘 ARGB）加輪廓線。face / depth 可以是 null（沒有面折線）。
        /// 只處理 [x0, x1] × [y0, y1] 加上外圍一圈（外輪廓長出去的部分），這個範圍外的像素都是透明，不用看。
        /// cov、mask 是呼叫端配好的暫存（長度 w × h）。
        /// </summary>
        public static void Apply(int[] buf, int w, int h, byte[] face, float[] depth, OutlineOptions o, int x0, int y0, int x1, int y1, byte[] cov, byte[] mask)
        {
            if (o == null || !o.Any) { return; }

            int t = o.Thickness, pct = o.Darkness;
            int rx0 = Math.Max(0, x0 - t), ry0 = Math.Max(0, y0 - t), rx1 = Math.Min(w - 1, x1 + t), ry1 = Math.Min(h - 1, y1 + t);
            if (rx1 < rx0 || ry1 < ry0) { return; }

            // 哪些像素本來就有東西（之後外輪廓寫進去的不算，不然會一圈一圈往外長）
            Parallel.For(ry0, ry1 + 1, y =>
            {
                int row = y * w;
                for (int x = rx0; x <= rx1; x++) { cov[row + x] = (byte)((((uint)buf[row + x]) >> 24) != 0 ? 1 : 0); }
            });

            // 1) 外輪廓：沒有東西的像素，Chebyshev 距離 t 以內有模型 → 取最近的那個像素的顏色加深
            if (o.Silhouette)
            {
                Parallel.For(ry0, ry1 + 1, y =>
                {
                    int row = y * w;
                    for (int x = rx0; x <= rx1; x++)
                    {
                        if (cov[row + x] != 0) { continue; }

                        int found = -1;
                        for (int k = 1; k <= t && found < 0; k++)
                        {
                            for (int dy = -k; dy <= k && found < 0; dy++)
                            {
                                int yy = y + dy;
                                if (yy < ry0 || yy > ry1) { continue; }

                                bool edgeRow = dy == -k || dy == k;
                                for (int dx = -k; dx <= k; dx += edgeRow ? 1 : 2 * k)
                                {
                                    int xx = x + dx;
                                    if (xx < rx0 || xx > rx1) { continue; }

                                    int j = yy * w + xx;
                                    if (cov[j] != 0) { found = j; break; }
                                }
                            }
                        }
                        if (found >= 0) { buf[row + x] = Darken(buf[found], pct); }
                    }
                });
            }

            // 2) 面折線：只動有東西的像素
            if (o.Creases && face != null && depth != null)
            {
                Array.Clear(mask, 0, mask.Length);
                Parallel.For(y0, y1 + 1, y =>
                {
                    if (y < 0 || y >= h) { return; }

                    for (int x = Math.Max(0, x0); x <= Math.Min(w - 1, x1); x++)
                    {
                        int i = y * w + x;
                        int fp = face[i];
                        if (cov[i] == 0 || fp == 0) { continue; }

                        if (IsCrease(face, depth, cov, w, h, x, y, fp, 1, 0) || IsCrease(face, depth, cov, w, h, x, y, fp, -1, 0)
                            || IsCrease(face, depth, cov, w, h, x, y, fp, 0, 1) || IsCrease(face, depth, cov, w, h, x, y, fp, 0, -1))
                        {
                            mask[i] = 1;
                        }
                    }
                });

                // 線寬 > 1：沿著同一個面往旁邊長（不跨到別的面）
                for (int pass = 1; pass < t; pass++)
                {
                    byte[] next = (byte[])mask.Clone();
                    for (int y = Math.Max(0, y0); y <= Math.Min(h - 1, y1); y++)
                    {
                        for (int x = Math.Max(0, x0); x <= Math.Min(w - 1, x1); x++)
                        {
                            int i = y * w + x;
                            if (mask[i] == 0) { continue; }

                            Grow(next, face, depth, cov, w, h, x + 1, y, i);
                            Grow(next, face, depth, cov, w, h, x - 1, y, i);
                            Grow(next, face, depth, cov, w, h, x, y + 1, i);
                            Grow(next, face, depth, cov, w, h, x, y - 1, i);
                        }
                    }
                    Array.Copy(next, mask, mask.Length);
                }

                Parallel.For(y0, y1 + 1, y =>
                {
                    if (y < 0 || y >= h) { return; }

                    for (int x = Math.Max(0, x0); x <= Math.Min(w - 1, x1); x++)
                    {
                        int i = y * w + x;
                        if (mask[i] != 0) { buf[i] = Darken(buf[i], pct); }
                    }
                });
            }
        }

        private static void Grow(byte[] next, byte[] face, float[] depth, byte[] cov, int w, int h, int x, int y, int from)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) { return; }

            int j = y * w + x;
            if (cov[j] == 0 || face[j] != face[from]) { return; }
            if (Math.Abs(depth[j] - depth[from]) > DepthJump) { return; }

            next[j] = 1;
        }

        /// <summary>
        /// (x, y) 這個像素在 (dx, dy) 方向的鄰居跟它之間有沒有「邊」，而且線該畫在 (x, y) 這一邊：
        /// 朝向不同 → 畫在比較暗的那個面；深度差一大截（而且不是斜面本身的坡度）→ 畫在近的那一邊。
        /// 鄰居是透明的不管（那是外輪廓的事）。
        /// </summary>
        private static bool IsCrease(byte[] face, float[] depth, byte[] cov, int w, int h, int x, int y, int fp, int dx, int dy)
        {
            int qx = x + dx, qy = y + dy;
            if (qx < 0 || qy < 0 || qx >= w || qy >= h) { return false; }

            int iq = qy * w + qx;
            if (cov[iq] == 0) { return false; }

            int fq = face[iq];
            if (fq == 0) { return false; }

            int i = y * w + x;
            if (fq != fp)
            {
                // 兩邊朝向不同：畫在比較暗的那一邊，亮的那一邊（通常是頂面）保持乾淨
                if (ShadeOf(fp) < ShadeOf(fq)) { return true; }
            }

            float jump = depth[iq] - depth[i];   // > 0：鄰居比較遠，自己是前面那個
            if (jump > DepthJump)
            {
                // 斜面本身的深度變化很大（幾乎側著看）時不算：用自己另一邊的坡度當參考
                int px = x - dx, py = y - dy;
                float slope = 0;
                if (px >= 0 && py >= 0 && px < w && py < h)
                {
                    int ip = py * w + px;
                    if (cov[ip] != 0 && face[ip] == fp) { slope = Math.Abs(depth[i] - depth[ip]); }
                }
                if (jump > 2.5f * slope + DepthJump * 0.5f) { return true; }
            }
            return false;
        }
    }
}
