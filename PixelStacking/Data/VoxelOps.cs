using System;
using System.Collections.Generic;
using PixelStacking.Render;

namespace PixelStacking.Data
{
    /// <summary>3D 魔法棒怎麼判斷「相連」。</summary>
    public enum WandConnectivity
    {
        /// <summary>6 連通：只算共用一個面的鄰居（上下左右前後）。</summary>
        Faces,
        /// <summary>26 連通：連共用邊或角的斜向鄰居也算。</summary>
        Corners,
    }

    /// <summary>一次立體搬移的結果：被改動的格子與當時的原值（可直接當復原資料）。</summary>
    public sealed class VoxelMove
    {
        public int[] Indices = new int[0];
        public byte[] Old = new byte[0];
        /// <summary>搬移後的選取遮罩（跟著搬過去）；沒給遮罩時是 null。</summary>
        public bool[] NewMask;
        /// <summary>搬出模型邊界、被丟掉的體素數（允許裁切時才會大於 0）。</summary>
        public int Clipped;
        public int Moved;
    }

    /// <summary>
    /// 立體（3D）體素操作：魔法棒選取相連的部件、把一批體素整塊搬到別的位置、抽出選取的體素。
    /// 都直接改傳進來的 byte[]（體素資料，索引 = z·W·H + y·W + x），呼叫端負責通知變動與復原。
    /// </summary>
    public static class VoxelOps
    {
        /// <summary>
        /// 3D 魔法棒：從 seed 這一格開始，找出立體相連的一整塊。sameColour = true 只算跟 seed 同色的格子；false 算所有有顏色的格子（不論顏色）。
        /// seed 是空格時回傳 null（沒有東西可以選）。
        /// </summary>
        public static bool[] Component(byte[] data, int w, int h, int d, int seed, bool sameColour, WandConnectivity conn)
        {
            if ((uint)seed >= (uint)data.Length || data[seed] == 0) { return null; }

            byte colour = data[seed];
            bool[] mask = new bool[data.Length];
            Stack<int> stack = new Stack<int>();
            stack.Push(seed); mask[seed] = true;

            int wh = w * h;
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                int z = i / wh, rem = i - z * wh, y = rem / w, x = rem - y * w;
                for (int dz = -1; dz <= 1; dz++)
                {
                    int zz = z + dz; if (zz < 0 || zz >= d) { continue; }
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int yy = y + dy; if (yy < 0 || yy >= h) { continue; }
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0 && dz == 0) { continue; }
                            if (conn == WandConnectivity.Faces && Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz) != 1) { continue; }

                            int xx = x + dx; if (xx < 0 || xx >= w) { continue; }

                            int j = zz * wh + yy * w + xx;
                            if (mask[j] || data[j] == 0) { continue; }
                            if (sameColour && data[j] != colour) { continue; }

                            mask[j] = true;
                            stack.Push(j);
                        }
                    }
                }
            }
            return mask;
        }

        /// <summary>遮罩選到幾格。</summary>
        public static int Count(bool[] mask)
        {
            if (mask == null) { return 0; }

            int n = 0;
            foreach (bool b in mask) { if (b) { n++; } }
            return n;
        }

        /// <summary>
        /// 把遮罩選到的體素（有顏色的）整塊平移 (dx, dy, dz)：原位置清成 0，新位置寫入（蓋掉那裡原有的東西）。
        /// allowClip = false 時，只要有任何一格會搬出模型邊界就整個不動、回傳 null；true 時超出的丟掉並記在 Clipped。
        /// mask = null 表示整個模型的所有有顏色體素。
        /// </summary>
        public static VoxelMove Shift(byte[] data, int w, int h, int d, bool[] mask, int dx, int dy, int dz, bool allowClip)
        {
            int wh = w * h;
            List<int> src = new List<int>();
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] != 0 && (mask == null || mask[i])) { src.Add(i); }
            }
            VoxelMove result = new VoxelMove();
            if (src.Count == 0 || (dx == 0 && dy == 0 && dz == 0)) { result.NewMask = mask == null ? null : (bool[])mask.Clone(); return result; }

            int[] dest = new int[src.Count];
            int clipped = 0;
            for (int k = 0; k < src.Count; k++)
            {
                int i = src[k];
                int z = i / wh, rem = i - z * wh, y = rem / w, x = rem - y * w;
                int nx = x + dx, ny = y + dy, nz = z + dz;
                if (nx < 0 || ny < 0 || nz < 0 || nx >= w || ny >= h || nz >= d) { dest[k] = -1; clipped++; }
                else { dest[k] = nz * wh + ny * w + nx; }
            }
            if (clipped > 0 && !allowClip) { return null; }

            Dictionary<int, byte> old = new Dictionary<int, byte>();
            byte[] values = new byte[src.Count];
            for (int k = 0; k < src.Count; k++) { values[k] = data[src[k]]; }
            for (int k = 0; k < src.Count; k++)
            {
                if (!old.ContainsKey(src[k])) { old[src[k]] = data[src[k]]; }
                data[src[k]] = 0;
            }
            bool[] newMask = mask == null ? null : new bool[mask.Length];
            for (int k = 0; k < src.Count; k++)
            {
                if (dest[k] < 0) { continue; }

                if (!old.ContainsKey(dest[k])) { old[dest[k]] = data[dest[k]]; }
                data[dest[k]] = values[k];
                if (newMask != null) { newMask[dest[k]] = true; }
            }

            result.Indices = new int[old.Count]; result.Old = new byte[old.Count];
            int n = 0;
            foreach (KeyValuePair<int, byte> kv in old) { result.Indices[n] = kv.Key; result.Old[n] = kv.Value; n++; }
            result.NewMask = newMask;
            result.Clipped = clipped;
            result.Moved = src.Count - clipped;
            return result;
        }

        /// <summary>把遮罩選到的有顏色體素複製成一份新陣列（其他格是 0）。</summary>
        public static byte[] Extract(byte[] data, bool[] mask)
        {
            byte[] r = new byte[data.Length];
            for (int i = 0; i < data.Length; i++) { if (mask[i]) { r[i] = data[i]; } }
            return r;
        }

        /// <summary>把遮罩選到的格子清成 0，回傳被改的格子與原值。</summary>
        public static VoxelMove Erase(byte[] data, bool[] mask)
        {
            List<int> idx = new List<int>(); List<byte> old = new List<byte>();
            for (int i = 0; i < data.Length; i++)
            {
                if (mask[i] && data[i] != 0) { idx.Add(i); old.Add(data[i]); data[i] = 0; }
            }
            return new VoxelMove { Indices = idx.ToArray(), Old = old.ToArray(), Moved = idx.Count };
        }
    }
}
