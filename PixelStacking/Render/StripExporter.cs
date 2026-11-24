using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelStacking.Data;

namespace PixelStacking.Render
{
    public sealed class StripOptions
    {
        /// <summary>輸出檔路徑（.png）；JSON 同名。</summary>
        public string Path;
        /// <summary>整數倍率：每個模型格子在圖上佔幾個像素（最近鄰）。</summary>
        public int Scale = 4;
        /// <summary>裁掉所有層、所有幀共同的空白（位移記在 JSON）。</summary>
        public bool Trim = true;
        public int Padding;
        /// <summary>true = 由上而下的層排成由左而右（最上層在最左）；false（預設）= 最底層（z=0）在最左。</summary>
        public bool TopFirst;
    }

    public sealed class StripResult
    {
        public string PngPath, JsonPath;
        public int LayerWidth, LayerHeight, Layers, Frames, SheetWidth, SheetHeight;

        public string Describe()
        {
            return Loc.Format(TextKey.ExportStrip_Describe, LayerWidth, LayerHeight, Layers, Frames, SheetWidth, SheetHeight);
        }
    }

    /// <summary>
    /// 疊層條帶（Sprite Stacking 的原始格式）：把模型從正上方往下看，每一層（z）切成一張平面圖，由左到右排成一條；
    /// 多幀時每一幀一列。遊戲端把每一層依序往上疊（每層偏移一點點）、再整體旋轉，就能做出偽 3D。
    /// 顏色是調色盤的原色（沒有光影、完整保留半透明），每個格子放大成 scale × scale 的方塊。可以在背景執行緒呼叫。
    /// </summary>
    public static class StripExporter
    {
        public static StripResult Export(Animation anim, StripOptions o, Action<int, int> progress, Func<bool> cancelled)
        {
            if (anim == null) { throw new ArgumentNullException("anim"); }
            if (o == null) { throw new ArgumentNullException("o"); }
            if (string.IsNullOrWhiteSpace(o.Path)) { throw new ArgumentException(Loc.Get(TextKey.Common_PleaseSpecifyTheOutputFile)); }
            if (o.Scale < 1 || o.Scale > 16) { throw new ArgumentException(Loc.Get(TextKey.Common_TheScaleMustBeBetween)); }
            if (o.Padding < 0 || o.Padding > 64) { throw new ArgumentException(Loc.Get(TextKey.Common_ThePaddingMustBeBetween)); }

            int W = anim.Width, H = anim.Height, D = anim.Depth, frames = anim.Count;
            int[] pal = anim.Palette;

            // 共同裁切範圍（模型格子座標）：所有層、所有幀有內容的最小外框 + 留白
            int x0 = 0, y0 = 0, x1 = W, y1 = H;
            if (o.Trim)
            {
                x0 = W; y0 = H; x1 = -1; y1 = -1;
                foreach (VoxelModel f in anim.Frames)
                {
                    byte[] d = f.Data;
                    for (int z = 0; z < D; z++)
                    {
                        int baseIdx = z * W * H;
                        for (int y = 0; y < H; y++)
                        {
                            int row = baseIdx + y * W;
                            for (int x = 0; x < W; x++)
                            {
                                if (d[row + x] == 0) { continue; }
                                if (((uint)pal[d[row + x]] >> 24) == 0) { continue; }
                                if (x < x0) { x0 = x; } if (x > x1) { x1 = x; }
                                if (y < y0) { y0 = y; } if (y > y1) { y1 = y; }
                            }
                        }
                    }
                }
                if (x1 < 0) { x0 = 0; y0 = 0; x1 = 0; y1 = 0; }   // 全部是空的：給 1×1
                x1++; y1++;
                x0 = Math.Max(0, x0 - o.Padding); y0 = Math.Max(0, y0 - o.Padding);
                x1 = Math.Min(W, x1 + o.Padding); y1 = Math.Min(H, y1 + o.Padding);
            }

            int lw = (x1 - x0) * o.Scale, lh = (y1 - y0) * o.Scale;
            long sw = (long)lw * D, sh = (long)lh * frames;
            if (sw > SpriteExporter.MaxTextureSide || sh > SpriteExporter.MaxTextureSide)
            {
                throw new InvalidOperationException(Loc.Format(TextKey.ExportStrip_TooLarge, sw, sh, SpriteExporter.MaxTextureSide));
            }

            int[] sheet = new int[(int)(sw * sh)];
            int total = frames * D, done = 0;
            for (int f = 0; f < frames; f++)
            {
                byte[] d = anim.Frames[f].Data;
                for (int col = 0; col < D; col++)
                {
                    if (cancelled != null && cancelled()) { throw new OperationCanceledException(); }

                    int z = o.TopFirst ? D - 1 - col : col;
                    int baseIdx = z * W * H;
                    for (int y = y0; y < y1; y++)
                    {
                        for (int x = x0; x < x1; x++)
                        {
                            byte v = d[baseIdx + y * W + x];
                            if (v == 0) { continue; }

                            int argb = pal[v];
                            if (((uint)argb >> 24) == 0) { continue; }

                            // 放大成 scale × scale
                            int px = col * lw + (x - x0) * o.Scale, py = f * lh + (y - y0) * o.Scale;
                            for (int sy = 0; sy < o.Scale; sy++)
                            {
                                int rowStart = (py + sy) * (int)sw + px;
                                for (int sx = 0; sx < o.Scale; sx++) { sheet[rowStart + sx] = argb; }
                            }
                        }
                    }
                    done++;
                    if (progress != null) { progress(done, total); }
                }
            }

            string path = o.Path.Trim();
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) { path += ".png"; }
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            string name = Path.GetFileNameWithoutExtension(path);
            if (name.Length == 0) { throw new ArgumentException(Loc.Get(TextKey.Common_TheFileNameIsNot)); }
            Directory.CreateDirectory(dir);

            StripResult result = new StripResult
            {
                PngPath = Path.Combine(dir, name + ".png"),
                JsonPath = Path.Combine(dir, name + ".json"),
                LayerWidth = lw, LayerHeight = lh, Layers = D, Frames = frames, SheetWidth = (int)sw, SheetHeight = (int)sh,
            };

            BitmapSource bmp = BitmapSource.Create((int)sw, (int)sh, 96, 96, PixelFormats.Bgra32, null, sheet, (int)sw * 4);
            bmp.Freeze();
            PngBitmapEncoder enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (FileStream fs = File.Create(result.PngPath)) { enc.Save(fs); }
            File.WriteAllText(result.JsonPath, BuildJson(anim, o, name, lw, lh, x0, y0, x1, y1), new UTF8Encoding(false));
            return result;
        }

        private static string Num(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

        private static string BuildJson(Animation anim, StripOptions o, string name, int lw, int lh, int x0, int y0, int x1, int y1)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"image\": \"{name}.png\",");
            sb.AppendLine("  \"type\": \"sprite-stacking-strip\",");
            sb.AppendLine($"  \"layerWidth\": {lw},");
            sb.AppendLine($"  \"layerHeight\": {lh},");
            sb.AppendLine($"  \"layerCount\": {anim.Depth},");
            sb.AppendLine($"  \"frameCount\": {anim.Count},");
            sb.AppendLine("  \"layout\": \"column = layer, row = frame (cell origin = top-left)\",");
            sb.AppendLine($"  \"layerOrder\": \"{(o.TopFirst ? "top-to-bottom" : "bottom-to-top")}\",");
            sb.AppendLine("  \"view\": \"top-down (x right, y down)\",");
            sb.AppendLine($"  \"fps\": {anim.Fps},");
            sb.AppendLine($"  \"frameDurationMs\": {Num(1000.0 / anim.Fps)},");
            sb.AppendLine($"  \"scale\": {o.Scale},");
            sb.AppendLine($"  \"trimmed\": {(o.Trim ? "true" : "false")},");
            sb.AppendLine($"  \"trimOffsetCells\": {{ \"x\": {x0}, \"y\": {y0} }},");
            sb.AppendLine($"  \"pivotCenter\": {{ \"x\": {Num((anim.Width / 2.0 - x0) * o.Scale)}, \"y\": {Num((anim.Height / 2.0 - y0) * o.Scale)} }},");
            sb.AppendLine($"  \"model\": {{ \"width\": {anim.Width}, \"height\": {anim.Height}, \"depth\": {anim.Depth} }},");
            sb.AppendLine("  \"shaded\": false");
            sb.Append("}");
            return sb.ToString();
        }
    }
}
