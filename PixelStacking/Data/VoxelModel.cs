using System;
using System.Collections.Generic;

namespace PixelStacking.Data
{
    /// <summary>
    /// 一幀的體素矩陣：Depth 層、每層 Width × Height 的 byte 格子。
    /// 0 = 透明，1~255 = 調色盤索引。Data[z * Width * Height + y * Width + x]。
    /// 同一個動畫的所有幀共用同一份調色盤陣列（見 <see cref="Animation"/>）。
    /// </summary>
    public sealed class VoxelModel
    {
        public int Width { get; }
        public int Height { get; }
        public int Depth { get; }
        public byte[] Data { get; }

        /// <summary>調色盤，BGRA（0xAARRGGBB），索引 0 保留給透明。與同動畫的其他幀共用同一個陣列。</summary>
        public int[] Palette { get; }

        /// <summary>每次內容變動 +1，預覽與縮圖用來判斷要不要重繪。</summary>
        public int Version { get; private set; }

        /// <summary>內容被改動後觸發（由 <see cref="NotifyChanged"/> 發出，Set 本身不發）。</summary>
        public event Action Changed;

        public int LayerSize => Width * Height;

        public VoxelModel(int width, int height, int depth)
            : this(width, height, depth, DefaultPalette.Create())
        {
        }

        public VoxelModel(int width, int height, int depth, int[] palette)
        {
            if (width < 1 || height < 1 || depth < 1) { throw new ArgumentException(Loc.Get(TextKey.Common_TheSizeMustBeGreater)); }

            Width = width;
            Height = height;
            Depth = depth;
            Data = new byte[width * height * depth];
            Palette = palette;
        }

        /// <summary>
        /// 每個體素屬於哪個部件（跟 <see cref="Data"/> 同樣的索引；0 = 身體／沒有標記）。還沒標記過任何部件時是 null（不佔記憶體），
        /// 第一次標記時由 <see cref="EnsureParts"/> 配置。部件編號對應 <see cref="Rig"/> 裡的 <see cref="Bone.Id"/>。
        /// </summary>
        public byte[] Parts { get; private set; }

        public byte[] EnsureParts()
        {
            if (Parts == null) { Parts = new byte[Data.Length]; }
            return Parts;
        }

        /// <summary>整個換掉部件標記陣列（讀檔、復原用）；null = 清空。長度不對會丟例外。</summary>
        public void SetParts(byte[] parts)
        {
            if (parts != null && parts.Length != Data.Length) { throw new ArgumentException(Loc.Get(TextKey.VoxelModel_ThePartMarkerLengthsDo)); }

            Parts = parts;
        }

        // --- 圖層 ------------------------------------------------------------------------------------------
        // 有多個圖層時，這一幀的 Data 是「合成結果」（所有看得見的圖層由下往上疊起來），真正可以編輯的內容在 Cels 裡：
        // Cels[k] 是第 k 個圖層在這一幀的內容（也是一個 VoxelModel，跟這一幀同樣大小、共用調色盤）。
        // 只有單一圖層時 Cels 是 null，Data 本身就是那一層（跟以前一模一樣，所有舊程式不用改）。

        private IList<Layer> _layers;

        /// <summary>如果這個模型是某一幀的圖格（cel），這裡是它所屬的那一幀；否則 null。</summary>
        public VoxelModel ParentFrame { get; private set; }

        /// <summary>各圖層在這一幀的內容（跟 Animation.Layers 同順序，第 0 個最底層）；單層時是 null。</summary>
        public List<VoxelModel> Cels { get; private set; }

        public bool IsLayered => Cels != null;

        /// <summary>這一幀目前是第幾個圖層的圖格（不是圖格就 -1）。</summary>
        public int CelIndex => ParentFrame == null || ParentFrame.Cels == null ? -1 : ParentFrame.Cels.IndexOf(this);

        /// <summary>讓這一幀變成多圖層：cels 與 layers 一一對應。立刻重新合成 Data。</summary>
        public void AttachLayers(IList<Layer> layers, List<VoxelModel> cels)
        {
            if (cels.Count != layers.Count || cels.Count < 2) { throw new ArgumentException(Loc.Get(TextKey.VoxelModel_TheNumberOfLayersDoes)); }

            if (Cels != null) { foreach (VoxelModel c in Cels) { if (c.ParentFrame == this) { c.ParentFrame = null; } } }
            _layers = layers;
            Cels = cels;
            foreach (VoxelModel c in cels)
            {
                if (c.Width != Width || c.Height != Height || c.Depth != Depth) { throw new ArgumentException(Loc.Get(TextKey.VoxelModel_TheCelSizesDoNot)); }

                c.ParentFrame = this;
            }
            Recompose();
        }

        /// <summary>回到單層：Data 維持目前的合成結果（呼叫前要先確定它就是想留下的那一層）。</summary>
        public void DetachLayers()
        {
            if (Cels != null) { foreach (VoxelModel c in Cels) { if (c.ParentFrame == this) { c.ParentFrame = null; } } }
            Cels = null;
            _layers = null;
        }

        /// <summary>把 Data 重新合成：看得見的圖層由下往上疊，上層有顏色的格子蓋住下層。不觸發 Changed。</summary>
        public void Recompose()
        {
            if (Cels == null) { return; }

            ComposeInto(Data, -1);
        }

        /// <summary>合成「除了第 skip 層以外」所有看得見的圖層到 dest（編輯畫布當背景參考用）；skip = -1 表示全部。</summary>
        public void ComposeInto(byte[] dest, int skip)
        {
            Array.Clear(dest, 0, dest.Length);
            if (Cels == null) { return; }

            for (int k = 0; k < Cels.Count; k++)
            {
                if (k == skip || !_layers[k].Visible) { continue; }

                byte[] src = Cels[k].Data;
                for (int i = 0; i < src.Length; i++) { if (src[i] != 0) { dest[i] = src[i]; } }
            }
        }

        /// <summary>複製一幀（內容與部件標記複製、調色盤共用；有圖層的話圖格也一起複製）。</summary>
        public VoxelModel Clone()
        {
            VoxelModel copy = new VoxelModel(Width, Height, Depth, Palette);
            Buffer.BlockCopy(Data, 0, copy.Data, 0, Data.Length);
            if (Parts != null) { copy.Parts = (byte[])Parts.Clone(); }
            if (Cels != null)
            {
                List<VoxelModel> cels = new List<VoxelModel>();
                foreach (VoxelModel c in Cels) { cels.Add(c.Clone()); }
                copy.AttachLayers(_layers, cels);
            }
            return copy;
        }

        public bool InBounds(int x, int y, int z)
        {
            return (uint)x < (uint)Width && (uint)y < (uint)Height && (uint)z < (uint)Depth;
        }

        public byte Get(int x, int y, int z)
        {
            return InBounds(x, y, z) ? Data[z * LayerSize + y * Width + x] : (byte)0;
        }

        /// <summary>寫入一格；超出範圍或值沒變回傳 false。不會觸發 Changed，批次寫完後請呼叫 <see cref="NotifyChanged"/>。</summary>
        public bool Set(int x, int y, int z, byte value)
        {
            if (!InBounds(x, y, z)) { return false; }

            int i = z * LayerSize + y * Width + x;
            if (Data[i] == value) { return false; }

            Data[i] = value;
            return true;
        }

        public void NotifyChanged()
        {
            Version++;
            // 圖格被改：所屬那一幀要重新合成並通知（預覽、時間軸縮圖、播放都看那一幀的 Data）
            if (ParentFrame != null && ParentFrame.Cels != null)
            {
                ParentFrame.Recompose();
                ParentFrame.NotifyChanged();
            }
            Changed?.Invoke();
        }

        /// <summary>依線性索引寫入一格（畫布用視角對應算出索引）；值沒變回傳 false，並用 old 回報原值。不觸發 Changed。</summary>
        public bool SetIndex(int index, byte value, out byte old)
        {
            old = Data[index];
            if (old == value) { return false; }

            Data[index] = value;
            return true;
        }
    }

    /// <summary>預設 32 色調色盤（PICO-8 標準 16 色 + 隱藏 16 色）。</summary>
    public static class DefaultPalette
    {
        public const int Count = 32;

        private static readonly int[] Rgb =
        {
            0x000000, 0x1D2B53, 0x7E2553, 0x008751, 0xAB5236, 0x5F574F, 0xC2C3C7, 0xFFF1E8,
            0xFF004D, 0xFFA300, 0xFFEC27, 0x00E436, 0x29ADFF, 0x83769C, 0xFF77A8, 0xFFCCAA,
            0x291814, 0x111D35, 0x422136, 0x125359, 0x742F29, 0x49333B, 0xA28879, 0xF3EF7D,
            0xBE1250, 0xFF6C24, 0xA8E72E, 0x00B543, 0x065AB5, 0x754665, 0xFF6E59, 0xFF9D81,
        };

        /// <summary>回傳 256 格調色盤；索引 1~32 是上面的顏色，0 與其餘為透明。</summary>
        public static int[] Create()
        {
            int[] palette = new int[256];
            for (int i = 0; i < Count; i++)
            {
                palette[i + 1] = unchecked((int)0xFF000000) | Rgb[i];
            }
            return palette;
        }
    }
}
