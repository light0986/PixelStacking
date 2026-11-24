using System;
using System.Collections.Generic;
using PixelStacking.Render;

namespace PixelStacking.Data
{
    /// <summary>把另一個 .pstk（組件）的內容變成目前動畫的一個圖層用的資料：置中對齊、顏色對應到目前的調色盤。</summary>
    public static class LayerImport
    {
        /// <summary>
        /// 回傳 dst 每一幀對應的一份體素資料（長度 = dst 的 W·H·D）。dst 的第 i 幀取 src 的第 (i mod src 幀數) 幀
        /// （組件只有一幀＝每一幀都放同一份；組件有好幾幀就循環）。尺寸不同時以中心對齊，超出的丟掉、不夠的補空白。
        /// 顏色用 <see cref="ImageImport.MapToPalette"/> 對應到 dst 的調色盤（已有的直接用、新的佔空格、空格不夠就量化）。
        /// </summary>
        public static List<byte[]> FromAnimation(Animation dst, Animation src, out ImageImport.MapStats stats)
        {
            int dw = dst.Width, dh = dst.Height, dd = dst.Depth;
            int sw = src.Width, sh = src.Height, sd = src.Depth;
            int ox = (dw - sw) / 2, oy = (dh - sh) / 2, oz = (dd - sd) / 2;

            // 每個來源幀轉成 ARGB 體積（只轉一次，dst 的幀多於 src 時重複使用）
            List<int[]> argbBySrc = new List<int[]>();
            foreach (VoxelModel f in src.Frames)
            {
                int[] argb = new int[dw * dh * dd];
                for (int z = 0; z < sd; z++)
                {
                    int tz = z + oz; if (tz < 0 || tz >= dd) { continue; }
                    for (int y = 0; y < sh; y++)
                    {
                        int ty = y + oy; if (ty < 0 || ty >= dh) { continue; }
                        for (int x = 0; x < sw; x++)
                        {
                            int tx = x + ox; if (tx < 0 || tx >= dw) { continue; }

                            byte v = f.Data[z * sw * sh + y * sw + x];
                            if (v != 0) { argb[tz * dw * dh + ty * dw + tx] = src.Palette[v]; }
                        }
                    }
                }
                argbBySrc.Add(argb);
            }

            byte[][] mapped = ImageImport.MapToPalette(dst, argbBySrc, out stats);
            List<byte[]> result = new List<byte[]>();
            for (int i = 0; i < dst.Count; i++) { result.Add((byte[])mapped[i % mapped.Length].Clone()); }
            return result;
        }
    }
}
