using System;
using System.Collections.Generic;

namespace PixelStacking.Data
{
    /// <summary>
    /// 一個圖層：整份動畫共用的圖層清單（每一幀在每個圖層都有一張「圖格」cel）。
    /// 清單的第 0 個是最底層，最後一個是最上層；上層有顏色（索引非 0）的格子會蓋住下層。
    /// </summary>
    public sealed class Layer
    {
        public string Name { get; set; }

        /// <summary>隱藏的圖層不參與合成（預覽、匯出、縮圖都看不到），但內容還在。</summary>
        public bool Visible { get; set; } = true;

        public Layer(string name) { Name = name; }
    }

    /// <summary>圖層結構的快照（復原用）：圖層清單與每一幀的圖格參考。不複製體素資料（只有單層時才存幀內容）。</summary>
    public sealed class LayerSnapshot
    {
        internal List<Layer> Layers;
        internal string[] Names;
        internal bool[] Visible;
        internal Dictionary<VoxelModel, List<VoxelModel>> Cels = new Dictionary<VoxelModel, List<VoxelModel>>();   // 有圖層的幀 → 當時的圖格
        internal Dictionary<VoxelModel, byte[]> SingleData = new Dictionary<VoxelModel, byte[]>();                // 單層的幀 → 當時的內容
    }
}
