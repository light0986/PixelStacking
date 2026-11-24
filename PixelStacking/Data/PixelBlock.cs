namespace PixelStacking.Data
{
    /// <summary>
    /// 一塊體素：寬 × 高 × 層數（深度）。每一層是「畫布格子順序」（列優先）的調色盤索引，0 = 透明。
    /// 第 0 層 = 取用／放下時的目前切片，之後依序往後面（切片編號變小）。選取的複製、剪下、貼上都用它。
    /// </summary>
    public sealed class PixelBlock
    {
        public int Width { get; }
        public int Height { get; }
        public int Depth { get; }

        /// <summary>Layers[k] 長度 = Width * Height，索引 = y * Width + x。</summary>
        public byte[][] Layers { get; }

        /// <summary>
        /// 哪些格子真的屬於這一塊（長度 = Width * Height，每一層共用同一個遮罩）；null = 整個矩形都算。
        /// 魔術棒選出來的不規則區域就是用它表示：遮罩外的格子不會被複製、剪下、搬移，貼上時也不會被寫入或蓋掉底下的東西
        /// （就算勾了「透明不覆蓋」以外的狀態也一樣——遮罩外的格子根本不是這一塊的一部分）。
        /// </summary>
        public bool[] Mask { get; set; }

        /// <summary>
        /// 羽化權重（長度 = Width * Height，每一層共用）：每格「被選到的程度」1~255，255 = 完全；null = 沒有羽化（全部完全選到）。
        /// 放下／刪除時，權重小於 255 的格子只取一部分：新內容以權重混進底下原有的顏色，刪除則只淡掉一部分。
        /// </summary>
        public byte[] Weights { get; set; }

        public PixelBlock(int width, int height, int depth)
        {
            Width = width;
            Height = height;
            Depth = depth;
            Layers = new byte[depth][];
            for (int k = 0; k < depth; k++) { Layers[k] = new byte[width * height]; }
        }

        /// <summary>這一塊實際包含幾格（每層）；沒有遮罩就是寬×高。</summary>
        public int CellCount
        {
            get
            {
                if (Mask == null) { return Width * Height; }

                int n = 0;
                foreach (bool b in Mask) { if (b) { n++; } }
                return n;
            }
        }
    }
}
