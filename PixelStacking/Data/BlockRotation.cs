using System;

namespace PixelStacking.Data
{
    /// <summary>
    /// 把一塊體素（<see cref="PixelBlock"/>：寬 × 高 × 層數）在畫布平面上轉任意角度（繞視線軸，每一層用同樣的轉法）。
    /// 用反向取樣：結果的每一格倒推回原本的哪一格（最近鄰），所以不會有破洞，90° 的倍數也是完全精確。
    /// 方向跟畫布看到的一樣：正角度 = 順時針（畫布的 y 軸朝下）。
    /// </summary>
    public static class BlockRotation
    {
        /// <summary>
        /// 轉 <paramref name="degrees"/> 度，繞著區塊正中心。結果的外框剛好裝得下轉過的矩形；
        /// 結果的遮罩 = 「倒推回去落在原本某個有效格子上」的格子（原本沒有遮罩時，遮罩就是轉過的矩形形狀）；羽化權重跟著搬。
        /// 轉 0°（或 360° 的倍數）回傳原本的區塊本身。
        /// </summary>
        public static PixelBlock Rotate(PixelBlock src, double degrees)
        {
            if (src == null) { throw new ArgumentNullException("src"); }
            if (double.IsNaN(degrees) || double.IsInfinity(degrees)) { throw new ArgumentException(Loc.Get(TextKey.Common_TheAngleIsNotA)); }

            double deg = degrees % 360.0;
            if (deg < 0) { deg += 360.0; }
            if (deg < 1e-9 || deg > 360.0 - 1e-9) { return src; }

            double rad = deg * Math.PI / 180.0;
            double c = Math.Cos(rad), s = Math.Sin(rad);
            c = Snap(c); s = Snap(s);   // 90° 的倍數要精確，不能留下 6e-17 的誤差把格子推到隔壁

            int w = src.Width, h = src.Height;
            int nw = Math.Max(1, (int)Math.Ceiling(w * Math.Abs(c) + h * Math.Abs(s) - 1e-9));
            int nh = Math.Max(1, (int)Math.Ceiling(w * Math.Abs(s) + h * Math.Abs(c) - 1e-9));

            // 先算每個結果格子對應的來源格（-1 = 不在原本的區塊裡），所有層共用
            int[] map = new int[nw * nh];
            for (int j = 0; j < nh; j++)
            {
                double dy = j + 0.5 - nh / 2.0;
                for (int i = 0; i < nw; i++)
                {
                    double dx = i + 0.5 - nw / 2.0;
                    // 順時針旋轉 deg 的反向：結果座標 → 來源座標
                    double sx = dx * c + dy * s + w / 2.0;
                    double sy = -dx * s + dy * c + h / 2.0;
                    int ix = (int)Math.Floor(sx + 1e-9), iy = (int)Math.Floor(sy + 1e-9);
                    int k = -1;
                    if (ix >= 0 && ix < w && iy >= 0 && iy < h)
                    {
                        int si = iy * w + ix;
                        if (src.Mask == null || src.Mask[si]) { k = si; }
                    }
                    map[j * nw + i] = k;
                }
            }

            PixelBlock dst = new PixelBlock(nw, nh, src.Depth);
            bool[] mask = new bool[nw * nh];
            byte[] weights = src.Weights == null ? null : new byte[nw * nh];
            for (int p = 0; p < map.Length; p++)
            {
                int k = map[p];
                if (k < 0) { continue; }

                mask[p] = true;
                if (weights != null) { weights[p] = src.Weights[k]; }
                for (int d = 0; d < src.Depth; d++) { dst.Layers[d][p] = src.Layers[d][k]; }
            }
            dst.Mask = mask;
            dst.Weights = weights;
            return dst;
        }

        private static double Snap(double v)
        {
            if (Math.Abs(v) < 1e-12) { return 0; }
            if (Math.Abs(v - 1) < 1e-12) { return 1; }
            if (Math.Abs(v + 1) < 1e-12) { return -1; }
            return v;
        }
    }
}
