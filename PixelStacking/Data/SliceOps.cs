using System;
using System.Collections.Generic;

namespace PixelStacking.Data
{
    /// <summary>一次寫入造成的變動：被改格子的線性索引與寫入前的值（兩個陣列等長），可直接當復原資料。</summary>
    public sealed class SliceChange
    {
        public int[] Indices = new int[0];
        public byte[] Old = new byte[0];

        public int Count => Indices.Length;

        /// <summary>把多個變動接成一個（不同切片的索引不會重複，所以直接串接）。</summary>
        public static SliceChange Combine(IList<SliceChange> parts)
        {
            int total = 0;
            foreach (SliceChange p in parts) { total += p.Count; }

            SliceChange all = new SliceChange { Indices = new int[total], Old = new byte[total] };
            int n = 0;
            foreach (SliceChange p in parts)
            {
                Array.Copy(p.Indices, 0, all.Indices, n, p.Count);
                Array.Copy(p.Old, 0, all.Old, n, p.Count);
                n += p.Count;
            }
            return all;
        }
    }

    /// <summary>
    /// 以「編輯畫布看到的樣子」讀寫一個切片：資料是 cw × ch 個格子（逐列、由左到右由上到下），
    /// 對應關係跟畫布完全一樣（見 <see cref="ViewMapper"/>）。複製 / 貼上 / 複製到別層都走這裡，
    /// 所以任何視角、任何幀之間只要畫布尺寸相同就能互相貼。
    /// </summary>
    public static class SliceOps
    {
        /// <summary>
        /// 把第 from 個切片搬到第 to 個位置，中間的切片各往空出來的方向挪一格（跟拖動清單項目一樣）。
        /// 體素與部件標記（Parts，有的話）一起搬。from == to 或超出範圍回傳 false；不觸發 Changed。
        /// 復原就是 Move(to, from)。
        /// </summary>
        public static bool Move(VoxelModel m, ViewDirection view, int from, int to)
        {
            int count = ViewMapper.SliceCount(view, m.Width, m.Height, m.Depth);
            if (from == to || from < 0 || to < 0 || from >= count || to >= count) { return false; }

            MoveIn(m.Data, m, view, from, to);
            if (m.Parts != null) { MoveIn(m.Parts, m, view, from, to); }
            return true;
        }

        private static void MoveIn(byte[] array, VoxelModel m, ViewDirection view, int from, int to)
        {
            int lo = Math.Min(from, to), hi = Math.Max(from, to);
            List<byte[]> old = new List<byte[]>();
            for (int s = lo; s <= hi; s++) { old.Add(ReadFrom(array, m, view, s)); }

            for (int s = lo; s <= hi; s++)
            {
                int source = s == to ? from : (from < to ? s + 1 : s - 1);   // 新的第 s 個 = 舊的第 source 個
                WriteTo(array, m, view, s, old[source - lo]);
            }
        }

        private static byte[] ReadFrom(byte[] array, VoxelModel m, ViewDirection view, int slice)
        {
            int cw = ViewMapper.CanvasWidth(view, m.Width, m.Height, m.Depth);
            int ch = ViewMapper.CanvasHeight(view, m.Width, m.Height, m.Depth);
            int origin, stepX, stepY;
            ViewMapper.Plane(view, slice, m.Width, m.Height, m.Depth, out origin, out stepX, out stepY);

            byte[] cells = new byte[cw * ch];
            for (int cy = 0; cy < ch; cy++)
            {
                for (int cx = 0; cx < cw; cx++) { cells[cy * cw + cx] = array[origin + cx * stepX + cy * stepY]; }
            }
            return cells;
        }

        private static void WriteTo(byte[] array, VoxelModel m, ViewDirection view, int slice, byte[] cells)
        {
            int cw = ViewMapper.CanvasWidth(view, m.Width, m.Height, m.Depth);
            int ch = ViewMapper.CanvasHeight(view, m.Width, m.Height, m.Depth);
            int origin, stepX, stepY;
            ViewMapper.Plane(view, slice, m.Width, m.Height, m.Depth, out origin, out stepX, out stepY);

            for (int cy = 0; cy < ch; cy++)
            {
                for (int cx = 0; cx < cw; cx++) { array[origin + cx * stepX + cy * stepY] = cells[cy * cw + cx]; }
            }
        }

        public static byte[] Read(VoxelModel m, ViewDirection view, int slice)
        {
            int cw = ViewMapper.CanvasWidth(view, m.Width, m.Height, m.Depth);
            int ch = ViewMapper.CanvasHeight(view, m.Width, m.Height, m.Depth);
            int origin, stepX, stepY;
            ViewMapper.Plane(view, slice, m.Width, m.Height, m.Depth, out origin, out stepX, out stepY);

            byte[] cells = new byte[cw * ch];
            for (int cy = 0; cy < ch; cy++)
            {
                for (int cx = 0; cx < cw; cx++) { cells[cy * cw + cx] = m.Data[origin + cx * stepX + cy * stepY]; }
            }
            return cells;
        }

        /// <summary>
        /// 把 cells 寫進指定切片。<paramref name="keepTransparent"/> 為 true 時，cells 裡透明（0）的格子不會覆蓋原有內容
        /// （只貼有顏色的格子＝疊加）。回傳實際改變的格子；沒有任何改變時 Count = 0。不觸發 Changed。
        /// </summary>
        public static SliceChange Write(VoxelModel m, ViewDirection view, int slice, byte[] cells, bool keepTransparent)
        {
            int cw = ViewMapper.CanvasWidth(view, m.Width, m.Height, m.Depth);
            int ch = ViewMapper.CanvasHeight(view, m.Width, m.Height, m.Depth);
            if (cells == null || cells.Length != cw * ch) { throw new ArgumentException(Loc.Get(TextKey.SliceOps_TheSliceSizesDoNot)); }

            int origin, stepX, stepY;
            ViewMapper.Plane(view, slice, m.Width, m.Height, m.Depth, out origin, out stepX, out stepY);

            List<int> indices = new List<int>();
            List<byte> old = new List<byte>();
            for (int cy = 0; cy < ch; cy++)
            {
                for (int cx = 0; cx < cw; cx++)
                {
                    byte nv = cells[cy * cw + cx];
                    if (keepTransparent && nv == 0) { continue; }

                    int index = origin + cx * stepX + cy * stepY;
                    byte ov = m.Data[index];
                    if (ov == nv) { continue; }

                    indices.Add(index);
                    old.Add(ov);
                    m.Data[index] = nv;
                }
            }
            return new SliceChange { Indices = indices.ToArray(), Old = old.ToArray() };
        }
    }
}
