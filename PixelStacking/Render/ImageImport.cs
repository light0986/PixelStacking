using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelStacking.Data;

namespace PixelStacking.Render
{
    /// <summary>圖片尺寸跟畫布不一樣時怎麼放。</summary>
    public enum ImageFit
    {
        /// <summary>放在畫布正中央，超出的裁掉、不夠的補透明。</summary>
        Center,
        /// <summary>左上角對齊，超出的裁掉、不夠的補透明。</summary>
        TopLeft,
        /// <summary>最近鄰縮放（可以放大或縮小）成剛好等於畫布大小；不保持長寬比。</summary>
        Stretch,
        /// <summary>等比縮放（最近鄰，可以放大或縮小）到剛好放得進畫布，置中；不裁切。</summary>
        Contain,
    }

    /// <summary>一張讀進來的圖：Bgra32（非預乘）像素，逐列由上到下、由左到右。</summary>
    public sealed class LoadedImage
    {
        public int Width, Height;
        public int[] Pixels;
    }

    /// <summary>
    /// 把圖片（PNG／BMP／GIF／JPG）變成編輯畫布上一層的調色盤索引：讀檔、依尺寸對齊、切條帶、對應到共用調色盤
    /// （已有的顏色直接用、新顏色佔用空格、空格不夠就量化）。純邏輯，不碰畫面。
    /// </summary>
    public static class ImageImport
    {
        public static string FileFilter { get { return Loc.Get(TextKey.ImageImport_ImagesPngBmpGifJpg); } }

        /// <exception cref="IOException">讀不到檔案或不是圖片。</exception>
        public static LoadedImage Load(string path)
        {
            try
            {
                using (FileStream fs = File.OpenRead(path))
                {
                    BitmapDecoder dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    FormatConvertedBitmap conv = new FormatConvertedBitmap(dec.Frames[0], PixelFormats.Bgra32, null, 0);
                    int w = conv.PixelWidth, h = conv.PixelHeight;
                    if (w < 1 || h < 1) { throw new IOException(Loc.Get(TextKey.ImageImport_TheImageIsEmpty)); }

                    byte[] raw = new byte[w * h * 4];
                    conv.CopyPixels(raw, w * 4, 0);
                    int[] px = new int[w * h];
                    for (int i = 0; i < px.Length; i++)
                    {
                        px[i] = (raw[i * 4 + 3] << 24) | (raw[i * 4 + 2] << 16) | (raw[i * 4 + 1] << 8) | raw[i * 4];
                    }
                    return new LoadedImage { Width = w, Height = h, Pixels = px };
                }
            }
            catch (Exception ex) when (ex is NotSupportedException || ex is FileFormatException || ex is InvalidOperationException || ex is ArgumentException)
            {
                throw new IOException(Loc.Format(TextKey.ImageImport_NotAnImage, Path.GetFileName(path), ex.Message));
            }
        }

        /// <summary>把任意大小的圖放進 cw × ch 的畫布（回傳長度 cw*ch 的 ARGB）。</summary>
        public static int[] Fit(int[] src, int sw, int sh, int cw, int ch, ImageFit fit)
        {
            int[] dst = new int[cw * ch];
            if (fit == ImageFit.Contain)
            {
                double scale = Math.Min((double)cw / sw, (double)ch / sh);
                int tw = Math.Max(1, Math.Min(cw, (int)Math.Round(sw * scale))), th = Math.Max(1, Math.Min(ch, (int)Math.Round(sh * scale)));
                int cx0 = (cw - tw) / 2, cy0 = (ch - th) / 2;
                for (int y = 0; y < th; y++)
                {
                    int sy = Math.Min(sh - 1, (int)((long)y * sh / th));
                    for (int x = 0; x < tw; x++) { dst[(cy0 + y) * cw + cx0 + x] = src[sy * sw + Math.Min(sw - 1, (int)((long)x * sw / tw))]; }
                }
                return dst;
            }
            if (fit == ImageFit.Stretch)
            {
                for (int y = 0; y < ch; y++)
                {
                    int sy = (int)((long)y * sh / ch);
                    for (int x = 0; x < cw; x++) { dst[y * cw + x] = src[sy * sw + (int)((long)x * sw / cw)]; }
                }
                return dst;
            }

            // 圖的 (0,0) 放在畫布的 (ox, oy)；置中時偶數差距偏向左上（跟筆刷、選取框同一個慣例）
            int ox = fit == ImageFit.Center ? (cw - sw) / 2 : 0, oy = fit == ImageFit.Center ? (ch - sh) / 2 : 0;
            if (fit == ImageFit.Center) { ox = (int)Math.Floor((cw - sw) / 2.0); oy = (int)Math.Floor((ch - sh) / 2.0); }
            for (int y = 0; y < sh; y++)
            {
                int ty = oy + y;
                if (ty < 0 || ty >= ch) { continue; }

                for (int x = 0; x < sw; x++)
                {
                    int tx = ox + x;
                    if (tx < 0 || tx >= cw) { continue; }

                    dst[ty * cw + tx] = src[y * sw + x];
                }
            }
            return dst;
        }

        /// <summary>
        /// 把一張橫向條帶切成多層：每層寬 layerWidth，只取第一列（高度 = layerHeight，不夠就補透明）。
        /// 最後不滿一層寬的零頭會補透明成一層（至少回傳一層）。
        /// </summary>
        public static List<int[]> SplitStrip(int[] src, int sw, int sh, int layerWidth, int layerHeight)
        {
            if (layerWidth < 1 || layerHeight < 1) { throw new ArgumentException(Loc.Get(TextKey.ImageImport_TheWidthAndHeightOf)); }

            int count = Math.Max(1, (sw + layerWidth - 1) / layerWidth);
            List<int[]> layers = new List<int[]>();
            for (int k = 0; k < count; k++)
            {
                int[] cells = new int[layerWidth * layerHeight];
                for (int y = 0; y < Math.Min(sh, layerHeight); y++)
                {
                    for (int x = 0; x < layerWidth; x++)
                    {
                        int sx = k * layerWidth + x;
                        if (sx < sw) { cells[y * layerWidth + x] = src[y * sw + sx]; }
                    }
                }
                layers.Add(cells);
            }
            return layers;
        }

        public sealed class MapStats
        {
            /// <summary>用到的不同顏色數（不含完全透明）。</summary>
            public int UniqueColours;
            /// <summary>調色盤裡本來就有、直接沿用的顏色數。</summary>
            public int Reused;
            /// <summary>新佔用的空格數。</summary>
            public int Added;
            /// <summary>空格不夠，顏色被量化（半透明會被壓成不透明）。</summary>
            public bool Quantized;
            /// <summary>量化後最多能用的新顏色數（只有 Quantized 時有意義）。</summary>
            public int Slots;
        }

        /// <summary>
        /// 把幾層 ARGB 轉成調色盤索引（0 = 透明）。已經在調色盤裡的顏色直接用；新顏色放進空格（alpha = 0 的格子，由小到大）；
        /// 空格不夠放時，把所有新顏色用中切法量化成「剛好用完空格」個不透明顏色（半透明的格子 alpha ≥ 128 變不透明、&lt; 128 當透明）。
        /// 會直接修改 anim.Palette（跟畫筆畫新顏色一樣，調色盤格不回收）。
        /// </summary>
        public static byte[][] MapToPalette(Animation anim, IList<int[]> layers, out MapStats stats)
        {
            stats = new MapStats();
            int[] pal = anim.Palette;

            // 1) 每個不同的 ARGB 出現幾次（完全透明忽略），並記下第一次出現的順序（新顏色就依這個順序放進調色盤）
            Dictionary<int, int> hist = new Dictionary<int, int>();
            List<int> firstSeen = new List<int>();
            foreach (int[] layer in layers)
            {
                foreach (int c in layer)
                {
                    if (((uint)c >> 24) == 0) { continue; }
                    int n;
                    if (!hist.TryGetValue(c, out n)) { firstSeen.Add(c); }
                    hist[c] = n + 1;
                }
            }
            stats.UniqueColours = hist.Count;

            // 2) 已有的顏色沿用；其餘是新顏色
            Dictionary<int, byte> lookup = new Dictionary<int, byte>();
            for (int i = 1; i < 256; i++) { if (!PaletteOps.IsFree(pal[i]) && !lookup.ContainsKey(pal[i])) { lookup[pal[i]] = (byte)i; } }
            List<int> fresh = new List<int>();
            foreach (int c in firstSeen)
            {
                if (lookup.ContainsKey(c)) { stats.Reused++; } else { fresh.Add(c); }
            }

            List<int> freeSlots = new List<int>();
            for (int i = 1; i < 256; i++) { if (PaletteOps.IsFree(pal[i])) { freeSlots.Add(i); } }

            Dictionary<int, byte> freshMap = new Dictionary<int, byte>();   // 新顏色 → 索引
            Dictionary<int, byte> flatten = null;                            // 量化時：原 ARGB → 索引（0 = 透明）
            if (fresh.Count <= freeSlots.Count)
            {
                for (int k = 0; k < fresh.Count; k++) { pal[freeSlots[k]] = fresh[k]; freshMap[fresh[k]] = (byte)freeSlots[k]; }
                stats.Added = fresh.Count;
            }
            else
            {
                stats.Quantized = true;
                stats.Slots = freeSlots.Count;
                flatten = new Dictionary<int, byte>();
                Dictionary<int, int> rgbHist = new Dictionary<int, int>();
                foreach (int c in fresh)
                {
                    if (((uint)c >> 24) < 128) { flatten[c] = 0; continue; }   // 半透明太淡的當透明
                    int rgb = c & 0xFFFFFF, n;
                    rgbHist.TryGetValue(rgb, out n);
                    rgbHist[rgb] = n + hist[c];
                }
                if (rgbHist.Count > 0 && freeSlots.Count > 0)
                {
                    List<int> boxColours = new List<int>();
                    Dictionary<int, int> boxOf = GifWriter.MedianCut(rgbHist, freeSlots.Count, boxColours);
                    for (int k = 0; k < boxColours.Count; k++) { pal[freeSlots[k]] = unchecked((int)0xFF000000) | boxColours[k]; }
                    stats.Added = boxColours.Count;
                    foreach (int c in fresh)
                    {
                        if (flatten.ContainsKey(c)) { continue; }
                        flatten[c] = (byte)freeSlots[boxOf[c & 0xFFFFFF]];
                    }
                }
                else { foreach (int c in fresh) { flatten[c] = 0; } }
            }

            // 3) 逐格轉索引
            byte[][] result = new byte[layers.Count][];
            for (int k = 0; k < layers.Count; k++)
            {
                int[] src = layers[k];
                byte[] dst = new byte[src.Length];
                for (int i = 0; i < src.Length; i++)
                {
                    int c = src[i];
                    if (((uint)c >> 24) == 0) { continue; }

                    byte v;
                    if (lookup.TryGetValue(c, out v)) { dst[i] = v; }
                    else if (freshMap.TryGetValue(c, out v)) { dst[i] = v; }
                    else if (flatten != null && flatten.TryGetValue(c, out v)) { dst[i] = v; }
                }
                result[k] = dst;
            }
            return result;
        }
    }
}
