using System;
using System.Collections.Generic;
using System.IO;
using PixelStacking.Data;

namespace PixelStacking.Render
{
    /// <summary>
    /// 自己寫的 GIF89a 編碼器（WPF 內建的 GifBitmapEncoder 不能設動畫延遲與循環、量化也很差）：
    /// 所有幀共用同一個調色盤（不會因為每幀調色盤不同而閃爍）、可選一個透明色、循環次數、每幀延遲。
    /// 顏色超過 256 就用中切法（median cut）量化；pixel art 通常顏色很少，會完全無損。
    /// </summary>
    public static class GifWriter
    {
        public sealed class Result
        {
            public byte[] Data;
            /// <summary>調色盤實際用了幾個顏色（不含透明）。</summary>
            public int Colours;
            /// <summary>顏色數超過上限，被量化過（有失真）。</summary>
            public bool Quantized;
        }

        /// <summary>
        /// 編碼。frames：每幀 w × h 的 ARGB（Bgra32，高位元組是 alpha）。
        /// transparent = true：alpha &lt; 128 的像素變透明、其餘用原本的 RGB（GIF 只有 1 位元透明）；
        /// false：所有像素都先疊在 background（0xRRGGBB）上再取色。
        /// delayCs：每幀延遲（百分之一秒，至少 2）。loops：0 = 無限循環。
        /// </summary>
        public static Result Encode(int w, int h, IList<int[]> frames, int delayCs, bool transparent, int background, int loops)
        {
            if (w < 1 || h < 1 || w > 65535 || h > 65535) { throw new ArgumentException(Loc.Get(TextKey.GifWriter_TheGIFWidthAndHeight)); }
            if (frames == null || frames.Count == 0) { throw new ArgumentException(Loc.Get(TextKey.GifWriter_ThereAreNoFrames)); }
            foreach (int[] f in frames) { if (f == null || f.Length != w * h) { throw new ArgumentException(Loc.Get(TextKey.GifWriter_TheFrameSizesDoNot)); } }
            delayCs = Math.Max(2, Math.Min(65535, delayCs));
            loops = Math.Max(0, Math.Min(65535, loops));

            // 1) 每個像素 → RGB（不透明）或「透明」
            int[][] rgb = new int[frames.Count][];
            Dictionary<int, int> histogram = new Dictionary<int, int>();
            for (int k = 0; k < frames.Count; k++)
            {
                int[] src = frames[k];
                int[] dst = new int[src.Length];
                for (int i = 0; i < src.Length; i++)
                {
                    int c = src[i], a = (int)((uint)c >> 24);
                    if (transparent)
                    {
                        if (a < 128) { dst[i] = -1; continue; }
                        dst[i] = c & 0xFFFFFF;
                    }
                    else
                    {
                        dst[i] = a == 255 ? (c & 0xFFFFFF) : BlendOver(c, background);
                    }
                    int cnt;
                    histogram.TryGetValue(dst[i], out cnt);
                    histogram[dst[i]] = cnt + 1;
                }
                rgb[k] = dst;
            }

            // 2) 調色盤（索引 0 留給透明色）
            int maxColours = transparent ? 255 : 256;
            List<int> palette = new List<int>();
            Dictionary<int, int> map;   // RGB → 調色盤索引（不含偏移）
            bool quantized = false;
            if (histogram.Count <= maxColours)
            {
                map = new Dictionary<int, int>();
                foreach (int c in histogram.Keys) { map[c] = palette.Count; palette.Add(c); }
            }
            else
            {
                quantized = true;
                map = MedianCut(histogram, maxColours, palette);
            }
            int offset = transparent ? 1 : 0;
            int tableColours = palette.Count + offset;
            int bits = 1;
            while ((1 << bits) < tableColours) { bits++; }
            int tableSize = 1 << bits;

            // 3) 寫檔
            using (MemoryStream ms = new MemoryStream())
            {
                Write(ms, "GIF89a");
                WriteU16(ms, w); WriteU16(ms, h);
                ms.WriteByte((byte)(0x80 | ((bits - 1) << 4) | (bits - 1)));   // 有全域調色盤、顏色解析度、調色盤大小
                ms.WriteByte(0);   // 背景色索引
                ms.WriteByte(0);   // 像素長寬比

                for (int i = 0; i < tableSize; i++)
                {
                    int idx = i - offset;
                    int c = (idx >= 0 && idx < palette.Count) ? palette[idx] : 0;
                    ms.WriteByte((byte)((c >> 16) & 255)); ms.WriteByte((byte)((c >> 8) & 255)); ms.WriteByte((byte)(c & 255));
                }

                // 循環（Netscape 擴充）：frames 只有一幀就不用
                if (frames.Count > 1)
                {
                    ms.WriteByte(0x21); ms.WriteByte(0xFF); ms.WriteByte(11); Write(ms, "NETSCAPE2.0");
                    ms.WriteByte(3); ms.WriteByte(1); WriteU16(ms, loops); ms.WriteByte(0);
                }

                int minCode = Math.Max(2, bits);
                for (int k = 0; k < frames.Count; k++)
                {
                    // 圖形控制：透明時用「還原成背景」(2) 才不會殘影；不透明時 1 = 不處理
                    ms.WriteByte(0x21); ms.WriteByte(0xF9); ms.WriteByte(4);
                    ms.WriteByte((byte)((transparent ? 2 : 1) << 2 | (transparent ? 1 : 0)));
                    WriteU16(ms, delayCs);
                    ms.WriteByte(0);   // 透明色索引 = 0
                    ms.WriteByte(0);

                    ms.WriteByte(0x2C); WriteU16(ms, 0); WriteU16(ms, 0); WriteU16(ms, w); WriteU16(ms, h); ms.WriteByte(0);

                    byte[] indices = new byte[w * h];
                    int[] px = rgb[k];
                    for (int i = 0; i < indices.Length; i++) { indices[i] = px[i] < 0 ? (byte)0 : (byte)(map[px[i]] + offset); }

                    ms.WriteByte((byte)minCode);
                    LzwEncode(ms, indices, minCode);
                    ms.WriteByte(0);   // 圖像資料結束
                }
                ms.WriteByte(0x3B);

                return new Result { Data = ms.ToArray(), Colours = palette.Count, Quantized = quantized };
            }
        }

        private static int BlendOver(int argb, int background)
        {
            int a = (int)((uint)argb >> 24);
            int r = (((argb >> 16) & 255) * a + ((background >> 16) & 255) * (255 - a) + 127) / 255;
            int g = (((argb >> 8) & 255) * a + ((background >> 8) & 255) * (255 - a) + 127) / 255;
            int b = ((argb & 255) * a + (background & 255) * (255 - a) + 127) / 255;
            return (r << 16) | (g << 8) | b;
        }

        private static void Write(Stream s, string ascii) { foreach (char c in ascii) { s.WriteByte((byte)c); } }

        private static void WriteU16(Stream s, int v) { s.WriteByte((byte)(v & 255)); s.WriteByte((byte)((v >> 8) & 255)); }

        // --- 中切法 ------------------------------------------------------------------------------------------
        private sealed class Box
        {
            public List<int> Colours;
            public long Weight;
            public int Range, Channel;   // 最寬的通道與寬度
        }

        internal static Dictionary<int, int> MedianCut(Dictionary<int, int> histogram, int count, List<int> palette)
        {
            List<Box> boxes = new List<Box>();
            Box first = new Box { Colours = new List<int>(histogram.Keys) };
            Measure(first, histogram);
            boxes.Add(first);

            while (boxes.Count < count)
            {
                Box best = null;
                foreach (Box b in boxes) { if (b.Colours.Count > 1 && (best == null || b.Range * Math.Sqrt(b.Weight) > best.Range * Math.Sqrt(best.Weight))) { best = b; } }
                if (best == null || best.Range == 0) { break; }

                int ch = best.Channel;
                int shift = ch == 0 ? 16 : ch == 1 ? 8 : 0;
                best.Colours.Sort((p, q) => ((p >> shift) & 255).CompareTo((q >> shift) & 255));

                // 依權重取中位數切開
                long half = best.Weight / 2, acc = 0;
                int cut = 1;
                for (int i = 0; i < best.Colours.Count - 1; i++)
                {
                    acc += histogram[best.Colours[i]];
                    cut = i + 1;
                    if (acc >= half) { break; }
                }
                Box right = new Box { Colours = best.Colours.GetRange(cut, best.Colours.Count - cut) };
                best.Colours.RemoveRange(cut, best.Colours.Count - cut);
                Measure(best, histogram);
                Measure(right, histogram);
                boxes.Add(right);
            }

            Dictionary<int, int> map = new Dictionary<int, int>();
            foreach (Box b in boxes)
            {
                double r = 0, g = 0, bl = 0;
                foreach (int c in b.Colours)
                {
                    int wgt = histogram[c];
                    r += ((c >> 16) & 255) * (double)wgt; g += ((c >> 8) & 255) * (double)wgt; bl += (c & 255) * (double)wgt;
                }
                double total = Math.Max(1, b.Weight);
                int idx = palette.Count;
                palette.Add(((int)Math.Round(r / total) << 16) | ((int)Math.Round(g / total) << 8) | (int)Math.Round(bl / total));
                foreach (int c in b.Colours) { map[c] = idx; }
            }
            return map;
        }

        private static void Measure(Box b, Dictionary<int, int> histogram)
        {
            int minR = 255, maxR = 0, minG = 255, maxG = 0, minB = 255, maxB = 0;
            long weight = 0;
            foreach (int c in b.Colours)
            {
                int r = (c >> 16) & 255, g = (c >> 8) & 255, bl = c & 255;
                if (r < minR) { minR = r; } if (r > maxR) { maxR = r; }
                if (g < minG) { minG = g; } if (g > maxG) { maxG = g; }
                if (bl < minB) { minB = bl; } if (bl > maxB) { maxB = bl; }
                weight += histogram[c];
            }
            int rr = maxR - minR, rg = maxG - minG, rb = maxB - minB;
            b.Weight = weight;
            b.Range = Math.Max(rr, Math.Max(rg, rb));
            b.Channel = rr >= rg && rr >= rb ? 0 : rg >= rb ? 1 : 2;
        }

        // --- LZW（GIF 版：低位元先出、可變碼長、4096 項滿了就送 clear）---------------------------------------
        private static void LzwEncode(Stream output, byte[] data, int minCode)
        {
            int clear = 1 << minCode, eoi = clear + 1;
            int codeSize = minCode + 1, next = eoi + 1;
            Dictionary<int, int> dict = new Dictionary<int, int>();

            BitSink sink = new BitSink(output);
            sink.Put(clear, codeSize);

            int prefix = data[0];
            for (int i = 1; i < data.Length; i++)
            {
                int k = data[i];
                int key = (prefix << 8) | k;
                int found;
                if (dict.TryGetValue(key, out found)) { prefix = found; continue; }

                sink.Put(prefix, codeSize);
                if (next < 4096)
                {
                    dict[key] = next++;
                    if (next > (1 << codeSize) && codeSize < 12) { codeSize++; }
                }
                else
                {
                    sink.Put(clear, codeSize);
                    dict.Clear();
                    codeSize = minCode + 1;
                    next = eoi + 1;
                }
                prefix = k;
            }
            sink.Put(prefix, codeSize);
            sink.Put(eoi, codeSize);
            sink.Flush();
        }

        /// <summary>把可變長度的碼一個位元一個位元塞進位元組，每滿 255 位元組就寫成一個子區塊。</summary>
        private sealed class BitSink
        {
            private readonly Stream _out;
            private readonly byte[] _block = new byte[255];
            private int _blockLen;
            private int _acc, _bits;

            public BitSink(Stream output) { _out = output; }

            public void Put(int code, int size)
            {
                _acc |= code << _bits;
                _bits += size;
                while (_bits >= 8)
                {
                    AddByte((byte)(_acc & 255));
                    _acc >>= 8;
                    _bits -= 8;
                }
            }

            private void AddByte(byte b)
            {
                _block[_blockLen++] = b;
                if (_blockLen == 255) { FlushBlock(); }
            }

            private void FlushBlock()
            {
                if (_blockLen == 0) { return; }

                _out.WriteByte((byte)_blockLen);
                _out.Write(_block, 0, _blockLen);
                _blockLen = 0;
            }

            public void Flush()
            {
                if (_bits > 0) { AddByte((byte)(_acc & 255)); _acc = 0; _bits = 0; }
                FlushBlock();
            }
        }
    }
}
