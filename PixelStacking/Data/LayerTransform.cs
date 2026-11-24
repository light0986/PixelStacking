using System;

namespace PixelStacking.Data
{
    /// <summary>
    /// 對「一個切片的畫布格子」（cw × ch，逐列由左到右、由上到下，跟 <see cref="SliceOps.Read"/> 一樣）做幾何變換。
    /// 全部是純函式：回傳新陣列，不改輸入。方向都是「在畫布上看到的樣子」，所以任何視角都一致。
    /// </summary>
    public static class LayerTransform
    {
        /// <summary>左右翻轉（鏡射：最左邊的一排變成最右邊）。</summary>
        public static byte[] FlipHorizontal(byte[] cells, int cw, int ch)
        {
            byte[] r = new byte[cells.Length];
            for (int y = 0; y < ch; y++)
            {
                for (int x = 0; x < cw; x++) { r[y * cw + (cw - 1 - x)] = cells[y * cw + x]; }
            }
            return r;
        }

        /// <summary>上下翻轉。</summary>
        public static byte[] FlipVertical(byte[] cells, int cw, int ch)
        {
            byte[] r = new byte[cells.Length];
            for (int y = 0; y < ch; y++)
            {
                Array.Copy(cells, y * cw, r, (ch - 1 - y) * cw, cw);
            }
            return r;
        }

        /// <summary>旋轉 180°。</summary>
        public static byte[] Rotate180(byte[] cells, int cw, int ch)
        {
            byte[] r = new byte[cells.Length];
            for (int i = 0; i < cells.Length; i++) { r[cells.Length - 1 - i] = cells[i]; }
            return r;
        }

        /// <summary>順時針轉 90°（左上角的格子跑到右上角）。只有正方形的畫布（cw == ch）才不會改變尺寸，所以只支援正方形。</summary>
        public static byte[] RotateClockwise(byte[] cells, int cw, int ch)
        {
            if (cw != ch) { throw new ArgumentException(Loc.Get(TextKey.LayerTransform_RotatingBy90NeedsA)); }

            int n = cw;
            byte[] r = new byte[cells.Length];
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++) { r[x * n + (n - 1 - y)] = cells[y * n + x]; }
            }
            return r;
        }

        /// <summary>逆時針轉 90°（左上角的格子跑到左下角）。只支援正方形畫布。</summary>
        public static byte[] RotateCounterClockwise(byte[] cells, int cw, int ch)
        {
            if (cw != ch) { throw new ArgumentException(Loc.Get(TextKey.LayerTransform_RotatingBy90NeedsA)); }

            int n = cw;
            byte[] r = new byte[cells.Length];
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++) { r[(n - 1 - x) * n + y] = cells[y * n + x]; }
            }
            return r;
        }

        /// <summary>
        /// 平移：dx &gt; 0 往右、dy &gt; 0 往下（畫布座標）。wrap = true 時超出的部分從另一邊繞回來（環狀），
        /// false 時超出的部分丟掉、空出來的地方是透明。
        /// </summary>
        public static byte[] Shift(byte[] cells, int cw, int ch, int dx, int dy, bool wrap)
        {
            byte[] r = new byte[cells.Length];
            for (int y = 0; y < ch; y++)
            {
                for (int x = 0; x < cw; x++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (wrap)
                    {
                        nx = ((nx % cw) + cw) % cw;
                        ny = ((ny % ch) + ch) % ch;
                    }
                    else if (nx < 0 || nx >= cw || ny < 0 || ny >= ch) { continue; }

                    r[ny * cw + nx] = cells[y * cw + x];
                }
            }
            return r;
        }
    }
}
