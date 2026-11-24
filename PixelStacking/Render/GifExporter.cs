using System;
using System.Collections.Generic;
using System.IO;
using PixelStacking.Data;

namespace PixelStacking.Render
{
    public sealed class GifOptions
    {
        /// <summary>輸出檔路徑（.gif）。</summary>
        public string Path;
        public double YawDegrees;
        public double ElevationDegrees = 30;
        /// <summary>整數倍率：每個模型格子在圖上佔幾個像素。</summary>
        public int Scale = 4;
        public bool Shade = true;
        public bool Trim = true;
        public int Padding;
        /// <summary>true = 背景透明（GIF 只有 1 位元透明：alpha &lt; 128 的像素變透明）；false = 疊在 <see cref="Background"/> 上。</summary>
        public bool TransparentBackground = true;
        /// <summary>不透明背景的顏色（0xRRGGBB）。</summary>
        public int Background = 0xFFFFFF;
        /// <summary>true = 無限循環；false = 只播一次。</summary>
        public bool Loop = true;
        /// <summary>輪廓線（底色加深的外輪廓與面折線）；null = 不畫。</summary>
        public OutlineOptions Outline;
    }

    public sealed class GifResult
    {
        public string Path;
        public int Width, Height, Frames, DelayCs, Colours;
        public bool Quantized;
        public long Bytes;

        public string Describe()
        {
            return Loc.Format(TextKey.ExportGif_Describe, Width, Height, Frames, DelayCs * 10, Colours, (Quantized ? Loc.Get(TextKey.GifExporter_TooManyColoursQuantized) : ""), (Bytes / 1024.0).ToString("0.#"));
        }
    }

    /// <summary>把整個動畫以固定角度渲染成動畫 GIF（速度用動畫本身的 fps）。可以在背景執行緒呼叫。</summary>
    public static class GifExporter
    {
        public static GifResult Export(Animation anim, GifOptions o, Action<int, int> progress, Func<bool> cancelled)
        {
            if (anim == null) { throw new ArgumentNullException("anim"); }
            if (o == null) { throw new ArgumentNullException("o"); }
            if (string.IsNullOrWhiteSpace(o.Path)) { throw new ArgumentException(Loc.Get(TextKey.Common_PleaseSpecifyTheOutputFile)); }

            int cw, ch;
            List<int[]> frames = SpriteExporter.RenderFrames(anim, o.YawDegrees, o.ElevationDegrees, o.Scale, o.Shade, o.Trim, o.Padding, o.Outline,
                progress, cancelled, out cw, out ch);
            if (cw > 65535 || ch > 65535) { throw new InvalidOperationException(Loc.Format(TextKey.ExportGif_TooLarge, cw, ch)); }

            if (cancelled != null && cancelled()) { throw new OperationCanceledException(); }

            int delayCs = Math.Max(2, (int)Math.Round(100.0 / Math.Max(1, anim.Fps)));
            GifWriter.Result enc = GifWriter.Encode(cw, ch, frames, delayCs, o.TransparentBackground, o.Background & 0xFFFFFF, o.Loop ? 0 : 1);

            string path = o.Path.Trim();
            if (!path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)) { path += ".gif"; }
            string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, enc.Data);

            return new GifResult { Path = path, Width = cw, Height = ch, Frames = frames.Count, DelayCs = delayCs, Colours = enc.Colours, Quantized = enc.Quantized, Bytes = enc.Data.Length };
        }
    }
}
