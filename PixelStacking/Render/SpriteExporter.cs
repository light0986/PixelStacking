using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelStacking.Data;

namespace PixelStacking.Render
{
    [Flags]
    public enum ExportKind
    {
        /// <summary>一張圖集 PNG（每列 = 一個方向、每欄 = 一幀）＋描述用的 JSON。</summary>
        Sheet = 1,
        /// <summary>每個「方向 × 幀」一個 PNG 檔，放在子資料夾。</summary>
        Sequence = 2,
        Both = 3,
    }

    public sealed class ExportOptions
    {
        /// <summary>輸出檔路徑（.png）。圖集用這個檔名，JSON 與序列資料夾由它衍生。</summary>
        public string Path;

        /// <summary>第一個方向的水平旋轉（度，預覽的 yaw）。</summary>
        public double YawDegrees;

        /// <summary>俯角（度，-90 ~ 90；90 = 正上方俯視、0 = 側視、負值 = 從下往上看）。</summary>
        public double ElevationDegrees = 30;

        /// <summary>方向數：繞一圈平均分成幾個方向（1 = 只有上面那一個角度）。</summary>
        public int Directions = 1;

        /// <summary>整數倍率：每個模型格子在圖上佔幾個像素。</summary>
        public int Scale = 4;

        public bool Shade = true;

        /// <summary>裁掉所有格子共同的空白（錨點會記在 JSON 裡）。關掉的話每格都是渲染器的完整畫面。</summary>
        public bool Trim = true;

        /// <summary>裁切後四邊額外保留的像素數。</summary>
        public int Padding;

        public ExportKind Kind = ExportKind.Both;

        /// <summary>輪廓線（底色加深的外輪廓與面折線）；null = 不畫。外輪廓會佔用模型外面的像素，所以圖會比沒有輪廓時大一圈。</summary>
        public OutlineOptions Outline;
    }

    public sealed class ExportResult
    {
        public string SheetPath, JsonPath, SequenceDir;
        public int CellWidth, CellHeight, Columns, Rows, SequenceFiles;
        public int SheetWidth, SheetHeight;

        public string Describe()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Loc.Format(TextKey.ExportSprite_DescribeCells, CellWidth, CellHeight, Rows, Columns));
            if (SheetPath != null) { sb.Append(Loc.Format(TextKey.ExportSprite_DescribeAtlas, SheetWidth, SheetHeight)); }
            if (SequenceDir != null) { sb.Append(Loc.Format(TextKey.ExportSprite_DescribeSequence, SequenceFiles)); }
            return sb.ToString();
        }
    }

    /// <summary>
    /// 把動畫以「固定角度」預先渲染成遊戲可以直接用的 PNG：圖集（列 = 方向、欄 = 幀）＋JSON，或 PNG 序列。
    /// 輸出是真彩色、完整 alpha（半透明與光影原樣保留），沒有 256 色限制。可以在背景執行緒呼叫（不碰 WPF 的 Bitmap）。
    /// </summary>
    public static class SpriteExporter
    {
        public const int MaxTextureSide = 16384;   // 常見 GPU 貼圖上限；圖集超過就拒絕
        public const int MaxCells = 2048;

        private struct Cell
        {
            public int X, Y, W, H;   // 在渲染器輸出裡的有內容範圍；W == 0 = 整格都是透明
            public int[] Px;         // 該範圍的像素（W*H）
        }

        /// <exception cref="ArgumentException">參數不合法。</exception>
        /// <exception cref="OperationCanceledException">cancelled 回傳 true。</exception>
        /// <exception cref="InvalidOperationException">結果超出限制（例如圖集太大）。</exception>
        public static ExportResult Export(Animation anim, ExportOptions o, Action<int, int> progress, Func<bool> cancelled)
        {
            if (anim == null) { throw new ArgumentNullException("anim"); }
            if (o == null) { throw new ArgumentNullException("o"); }
            if (string.IsNullOrWhiteSpace(o.Path)) { throw new ArgumentException(Loc.Get(TextKey.Common_PleaseSpecifyTheOutputFile)); }
            if (o.Directions < 1 || o.Directions > 64) { throw new ArgumentException(Loc.Get(TextKey.SpriteExporter_TheNumberOfDirectionsMust)); }
            if (o.Scale < 1 || o.Scale > 16) { throw new ArgumentException(Loc.Get(TextKey.Common_TheScaleMustBeBetween)); }
            if (o.Padding < 0 || o.Padding > 64) { throw new ArgumentException(Loc.Get(TextKey.Common_ThePaddingMustBeBetween)); }
            if (double.IsNaN(o.YawDegrees) || double.IsNaN(o.ElevationDegrees)) { throw new ArgumentException(Loc.Get(TextKey.Common_TheAngleIsNotA)); }
            if ((o.Kind & ExportKind.Both) == 0) { throw new ArgumentException(Loc.Get(TextKey.SpriteExporter_NoOutputWasSelected)); }

            int frames = anim.Count, dirs = o.Directions;
            if ((long)frames * dirs > MaxCells) { throw new ArgumentException(Loc.Format(TextKey.ExportSprite_TooManyCells, frames * dirs, MaxCells)); }

            double elevDeg = Math.Max(-90, Math.Min(90, o.ElevationDegrees));
            double elev = elevDeg * Math.PI / 180;

            StackRenderer r = new StackRenderer { Mode = RenderMode.Voxel, BitmapEnabled = false, Outline = o.Outline };
            r.ConfigureScale(anim.Width, anim.Height, anim.Depth, o.Scale);
            int outW = r.OutputWidth, outH = r.OutputHeight;
            ClipRange clip = ClipRange.Full(anim.Width, anim.Height, anim.Depth);

            // --- 渲染每個「方向 × 幀」，只留有內容的範圍（記憶體小很多）---------------------------------------
            Cell[,] cells = new Cell[dirs, frames];
            int total = dirs * frames, done = 0;
            double[] angles = new double[dirs];
            for (int d = 0; d < dirs; d++)
            {
                angles[d] = NormalizeDegrees(o.YawDegrees + 360.0 * d / dirs);
                double yaw = angles[d] * Math.PI / 180;

                for (int f = 0; f < frames; f++)
                {
                    if (cancelled != null && cancelled()) { throw new OperationCanceledException(); }

                    r.Render(anim.Frames[f], yaw, elev, clip, o.Shade);
                    cells[d, f] = CaptureCell(r.Pixels, outW, outH, o.Trim);

                    done++;
                    if (progress != null) { progress(done, total); }
                }
            }

            // --- 共同的裁切範圍（所有格子聯集 + 留白），每格都是同樣大小，錨點才對得上 --------------------------
            int ux0 = 0, uy0 = 0, ux1 = outW, uy1 = outH;
            if (o.Trim)
            {
                ux0 = int.MaxValue; uy0 = int.MaxValue; ux1 = int.MinValue; uy1 = int.MinValue;
                foreach (Cell c in cells)
                {
                    if (c.W == 0) { continue; }
                    if (c.X < ux0) { ux0 = c.X; }
                    if (c.Y < uy0) { uy0 = c.Y; }
                    if (c.X + c.W > ux1) { ux1 = c.X + c.W; }
                    if (c.Y + c.H > uy1) { uy1 = c.Y + c.H; }
                }
                if (ux1 < ux0) { ux0 = 0; uy0 = 0; ux1 = 1; uy1 = 1; }   // 全部都是空的：給 1×1 的透明格
                ux0 = Math.Max(0, ux0 - o.Padding); uy0 = Math.Max(0, uy0 - o.Padding);
                ux1 = Math.Min(outW, ux1 + o.Padding); uy1 = Math.Min(outH, uy1 + o.Padding);
            }
            int cw = ux1 - ux0, ch = uy1 - uy0;

            ExportResult result = new ExportResult { CellWidth = cw, CellHeight = ch, Columns = frames, Rows = dirs };
            string dir = Path.GetDirectoryName(Path.GetFullPath(o.Path));
            string name = Path.GetFileNameWithoutExtension(o.Path);
            if (name.Length == 0) { throw new ArgumentException(Loc.Get(TextKey.Common_TheFileNameIsNot)); }

            // 錨點（以裁切後的格子左上角為原點，單位 = 像素）：模型中心、模型底面中心（腳底）
            double pivotCx = outW / 2.0 - ux0, pivotCy = outH / 2.0 - uy0;
            double pivotBx = pivotCx, pivotBy = outH / 2.0 + anim.Depth / 2.0 * Math.Cos(elev) * o.Scale - uy0;

            // --- 圖集 ---------------------------------------------------------------------------------
            if ((o.Kind & ExportKind.Sheet) != 0)
            {
                long sw = (long)cw * frames, sh = (long)ch * dirs;
                if (sw > MaxTextureSide || sh > MaxTextureSide)
                {
                    throw new InvalidOperationException(
                        Loc.Format(TextKey.ExportSprite_AtlasTooLarge, sw, sh, MaxTextureSide));
                }

                int[] sheet = new int[(int)(sw * sh)];
                for (int d = 0; d < dirs; d++)
                {
                    for (int f = 0; f < frames; f++)
                    {
                        Blit(cells[d, f], ux0, uy0, sheet, (int)sw, f * cw, d * ch, cw, ch);
                    }
                }

                Directory.CreateDirectory(dir);
                result.SheetPath = Path.Combine(dir, name + ".png");
                result.JsonPath = Path.Combine(dir, name + ".json");
                result.SheetWidth = (int)sw; result.SheetHeight = (int)sh;
                SavePng(result.SheetPath, (int)sw, (int)sh, sheet);
                File.WriteAllText(result.JsonPath, BuildJson(anim, o, name, cw, ch, frames, dirs, angles, elevDeg, pivotCx, pivotCy, pivotBx, pivotBy),
                    new UTF8Encoding(false));
            }

            // --- PNG 序列 ----------------------------------------------------------------------------
            if ((o.Kind & ExportKind.Sequence) != 0)
            {
                string seqDir = Path.Combine(dir, name + "_frames");
                Directory.CreateDirectory(seqDir);
                result.SequenceDir = seqDir;

                int pad = Math.Max(2, (frames - 1).ToString(CultureInfo.InvariantCulture).Length);
                string fmtF = new string('0', pad);
                int[] one = new int[cw * ch];
                for (int d = 0; d < dirs; d++)
                {
                    for (int f = 0; f < frames; f++)
                    {
                        if (cancelled != null && cancelled()) { throw new OperationCanceledException(); }

                        Array.Clear(one, 0, one.Length);
                        Blit(cells[d, f], ux0, uy0, one, cw, 0, 0, cw, ch);

                        string file = dirs > 1
                            ? $"{name}_d{d.ToString("00", CultureInfo.InvariantCulture)}_f{f.ToString(fmtF, CultureInfo.InvariantCulture)}.png"
                            : $"{name}_f{f.ToString(fmtF, CultureInfo.InvariantCulture)}.png";
                        SavePng(Path.Combine(seqDir, file), cw, ch, one);
                        result.SequenceFiles++;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 把每一幀用固定角度渲染成同樣大小的像素陣列（Bgra32，非預乘；跟 <see cref="Export"/> 同一套渲染與共同裁切）。
        /// 給 GIF 這類「每幀一張圖」的輸出用。回傳的每個陣列長度都是 cw × ch。
        /// </summary>
        internal static List<int[]> RenderFrames(Animation anim, double yawDeg, double elevDeg, int scale, bool shade, bool trim, int padding, OutlineOptions outline,
            Action<int, int> progress, Func<bool> cancelled, out int cw, out int ch)
        {
            if (anim == null) { throw new ArgumentNullException("anim"); }
            if (scale < 1 || scale > 16) { throw new ArgumentException(Loc.Get(TextKey.Common_TheScaleMustBeBetween)); }
            if (padding < 0 || padding > 64) { throw new ArgumentException(Loc.Get(TextKey.Common_ThePaddingMustBeBetween)); }
            if (double.IsNaN(yawDeg) || double.IsNaN(elevDeg)) { throw new ArgumentException(Loc.Get(TextKey.Common_TheAngleIsNotA)); }

            double elev = Math.Max(-90, Math.Min(90, elevDeg)) * Math.PI / 180;
            double yaw = NormalizeDegrees(yawDeg) * Math.PI / 180;
            StackRenderer r = new StackRenderer { Mode = RenderMode.Voxel, BitmapEnabled = false, Outline = outline };
            r.ConfigureScale(anim.Width, anim.Height, anim.Depth, scale);
            int outW = r.OutputWidth, outH = r.OutputHeight;
            ClipRange clip = ClipRange.Full(anim.Width, anim.Height, anim.Depth);

            int frames = anim.Count;
            Cell[] cells = new Cell[frames];
            for (int f = 0; f < frames; f++)
            {
                if (cancelled != null && cancelled()) { throw new OperationCanceledException(); }

                r.Render(anim.Frames[f], yaw, elev, clip, shade);
                cells[f] = CaptureCell(r.Pixels, outW, outH, trim);
                if (progress != null) { progress(f + 1, frames * 2); }
            }

            int ux0 = 0, uy0 = 0, ux1 = outW, uy1 = outH;
            if (trim)
            {
                ux0 = int.MaxValue; uy0 = int.MaxValue; ux1 = int.MinValue; uy1 = int.MinValue;
                foreach (Cell c in cells)
                {
                    if (c.W == 0) { continue; }
                    if (c.X < ux0) { ux0 = c.X; }
                    if (c.Y < uy0) { uy0 = c.Y; }
                    if (c.X + c.W > ux1) { ux1 = c.X + c.W; }
                    if (c.Y + c.H > uy1) { uy1 = c.Y + c.H; }
                }
                if (ux1 < ux0) { ux0 = 0; uy0 = 0; ux1 = 1; uy1 = 1; }
                ux0 = Math.Max(0, ux0 - padding); uy0 = Math.Max(0, uy0 - padding);
                ux1 = Math.Min(outW, ux1 + padding); uy1 = Math.Min(outH, uy1 + padding);
            }
            cw = ux1 - ux0;
            ch = uy1 - uy0;

            List<int[]> result = new List<int[]>(frames);
            for (int f = 0; f < frames; f++)
            {
                int[] one = new int[cw * ch];
                Blit(cells[f], ux0, uy0, one, cw, 0, 0, cw, ch);
                result.Add(one);
                if (progress != null) { progress(frames + f + 1, frames * 2); }
            }
            return result;
        }

        // --- 內部 ------------------------------------------------------------------------------------------
        private static double NormalizeDegrees(double deg)
        {
            deg = deg % 360.0;
            if (deg < 0) { deg += 360.0; }
            return Math.Round(deg, 4);
        }

        /// <summary>找出 alpha &gt; 0 的最小外框並複製出來；trim = false 時整張都留下。</summary>
        private static Cell CaptureCell(int[] buf, int w, int h, bool trim)
        {
            int x0 = 0, y0 = 0, x1 = w, y1 = h;
            if (trim)
            {
                x0 = w; y0 = h; x1 = -1; y1 = -1;
                for (int y = 0; y < h; y++)
                {
                    int row = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        if (((uint)buf[row + x] >> 24) == 0) { continue; }
                        if (x < x0) { x0 = x; }
                        if (x > x1) { x1 = x; }
                        if (y < y0) { y0 = y; }
                        if (y > y1) { y1 = y; }
                    }
                }
                if (x1 < 0) { return new Cell(); }   // 整格透明
                x1++; y1++;
            }

            Cell c = new Cell { X = x0, Y = y0, W = x1 - x0, H = y1 - y0 };
            c.Px = new int[c.W * c.H];
            for (int y = 0; y < c.H; y++) { Array.Copy(buf, (y0 + y) * w + x0, c.Px, y * c.W, c.W); }
            return c;
        }

        /// <summary>把裁出來的格子貼到目標緩衝的 (dx, dy)；目標格子 = 共同裁切範圍（原點 ux0,uy0、大小 cw×ch）。</summary>
        private static void Blit(Cell c, int ux0, int uy0, int[] dst, int dstStride, int dx, int dy, int cw, int ch)
        {
            if (c.W == 0) { return; }

            for (int y = 0; y < c.H; y++)
            {
                int ty = c.Y + y - uy0;
                if (ty < 0 || ty >= ch) { continue; }

                int sx = 0, tx = c.X - ux0;
                int len = c.W;
                if (tx < 0) { sx = -tx; len += tx; tx = 0; }
                if (tx + len > cw) { len = cw - tx; }
                if (len <= 0) { continue; }

                Array.Copy(c.Px, y * c.W + sx, dst, (dy + ty) * dstStride + dx + tx, len);
            }
        }

        private static void SavePng(string path, int w, int h, int[] pixels)
        {
            BitmapSource bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
            bmp.Freeze();
            PngBitmapEncoder enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (FileStream fs = File.Create(path)) { enc.Save(fs); }
        }

        private static string Num(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

        private static string BuildJson(Animation anim, ExportOptions o, string name, int cw, int ch, int frames, int dirs,
            double[] angles, double elevDeg, double pcx, double pcy, double pbx, double pby)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"image\": \"{name}.png\",");
            sb.AppendLine($"  \"cellWidth\": {cw},");
            sb.AppendLine($"  \"cellHeight\": {ch},");
            sb.AppendLine($"  \"columns\": {frames},");
            sb.AppendLine($"  \"rows\": {dirs},");
            sb.AppendLine("  \"layout\": \"row = direction, column = frame (cell origin = top-left)\",");
            sb.AppendLine($"  \"frameCount\": {frames},");
            sb.AppendLine($"  \"directionCount\": {dirs},");
            sb.AppendLine($"  \"fps\": {anim.Fps},");
            sb.AppendLine($"  \"frameDurationMs\": {Num(1000.0 / anim.Fps)},");
            sb.AppendLine($"  \"scale\": {o.Scale},");
            sb.AppendLine($"  \"elevationDegrees\": {Num(elevDeg)},");
            sb.AppendLine("  \"directionYawDegrees\": [" + string.Join(", ", Array.ConvertAll(angles, Num)) + "],");
            sb.AppendLine($"  \"pivotCenter\": {{ \"x\": {Num(pcx)}, \"y\": {Num(pcy)} }},");
            sb.AppendLine($"  \"pivotBottom\": {{ \"x\": {Num(pbx)}, \"y\": {Num(pby)} }},");
            sb.AppendLine($"  \"model\": {{ \"width\": {anim.Width}, \"height\": {anim.Height}, \"depth\": {anim.Depth} }},");
            sb.AppendLine($"  \"shaded\": {(o.Shade ? "true" : "false")},");
            sb.AppendLine($"  \"trimmed\": {(o.Trim ? "true" : "false")}");
            sb.Append("}");
            return sb.ToString();
        }
    }
}
