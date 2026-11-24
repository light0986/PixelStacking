using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelStacking.Data;
using PixelStacking.Render;

namespace PixelStacking.Cli
{
    /// <summary>共用小工具與「看」的命令（info / palette / slice / render / 匯出 / 說明）。</summary>
    internal static class Cmds
    {
        // --- 共用 --------------------------------------------------------------------------------------------
        internal static Animation Open(string path)
        {
            if (!File.Exists(path)) { throw new CliError("file not found: " + path); }

            try { return Animation.Load(path); }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException || e is EndOfStreamException || e is ArgumentException)
            {
                throw new CliError("cannot read '" + path + "' as a .pstk file: " + Loc.T(e.Message));
            }
        }

        internal static void Save(Animation anim, string path)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }

            anim.Save(path);
        }

        /// <summary>改檔案的命令收尾：存檔並印一行結果。</summary>
        internal static void SaveAndReport(Animation anim, string path, string message)
        {
            Save(anim, path);
            Console.WriteLine(message);
            Console.WriteLine("saved: " + path);
        }

        internal static ViewDirection ParseView(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "top": case "1": return ViewDirection.Top;
                case "bottom": case "2": return ViewDirection.Bottom;
                case "front": case "3": return ViewDirection.Front;
                case "back": case "4": return ViewDirection.Back;
                case "left": case "5": return ViewDirection.Left;
                case "right": case "6": return ViewDirection.Right;
            }
            throw new CliError("view must be one of top, bottom, front, back, left, right (got '" + s + "')", 2);
        }

        internal static string AxisName(int[] v)
        {
            for (int i = 0; i < 3; i++) { if (v[i] != 0) { return (v[i] > 0 ? "+" : "-") + "XYZ"[i]; } }
            return "?";
        }

        // --- 顏色 --------------------------------------------------------------------------------------------
        internal static bool IsErase(string s)
        {
            string t = (s ?? "").Trim().ToLowerInvariant();
            return t == "erase" || t == "none" || t == "clear" || t == "transparent" || t == "0";
        }

        private static readonly Dictionary<string, int> Named = new Dictionary<string, int>
        {
            { "black", 0x000000 }, { "white", 0xFFFFFF }, { "red", 0xE03030 }, { "green", 0x30B040 }, { "blue", 0x3060E0 },
            { "yellow", 0xF0D020 }, { "orange", 0xF08020 }, { "purple", 0x9040C0 }, { "pink", 0xF080B0 }, { "brown", 0x805030 },
            { "gray", 0x808080 }, { "grey", 0x808080 }, { "cyan", 0x30C8D8 }, { "magenta", 0xD030C0 }, { "skin", 0xF0C8A0 },
            { "darkgray", 0x404040 }, { "lightgray", 0xC0C0C0 }, { "navy", 0x203080 }, { "teal", 0x208080 }, { "lime", 0x90E030 },
        };

        /// <summary>
        /// 輪廓線選項（render / export-png / export-gif 共用）：--outline 開啟（外輪廓＋面折線），
        /// --no-silhouette／--no-creases 關掉其中一種，--outline-width 1~3，--outline-tone light|medium|dark。沒有 --outline 回傳 null。
        /// </summary>
        internal static OutlineOptions ReadOutline(CliArgs a)
        {
            bool on = a.Has("outline"), noSil = a.Has("no-silhouette"), noCrease = a.Has("no-creases");
            int width = a.Int("outline-width", 1);
            string tone = (a.Str("outline-tone", "medium") ?? "medium").ToLowerInvariant();
            if (!on) { return null; }

            if (width < 1 || width > 3) { throw new CliError("--outline-width must be 1, 2 or 3", 2); }

            int darkness;
            if (tone == "light") { darkness = 70; }
            else if (tone == "medium") { darkness = 55; }
            else if (tone == "dark") { darkness = 35; }
            else { throw new CliError("--outline-tone must be light, medium or dark", 2); }

            OutlineOptions o = new OutlineOptions { Silhouette = !noSil, Creases = !noCrease, Thickness = width, Darkness = darkness };
            if (!o.Any) { throw new CliError("--no-silhouette and --no-creases together leave no line to draw", 2); }

            return o;
        }

        /// <summary>顏色：#RGB、#RRGGBB、#RRGGBBAA（CSS 順序，AA 是不透明度）、R,G,B 或 R,G,B,A（0~255）、或幾個常用英文名稱。回傳 0xAARRGGBB。</summary>
        internal static int ParseColour(string s)
        {
            string t = (s ?? "").Trim();
            string low = t.ToLowerInvariant();
            int named;
            if (Named.TryGetValue(low, out named)) { return unchecked((int)0xFF000000) | named; }

            if (t.StartsWith("#", StringComparison.Ordinal) || (t.Length >= 3 && t.All(Uri.IsHexDigit) && !t.Contains(",")))
            {
                string h = t.TrimStart('#');
                if (!h.All(Uri.IsHexDigit)) { throw new CliError("bad colour '" + s + "' (use #RRGGBB)", 2); }

                if (h.Length == 3) { h = "" + h[0] + h[0] + h[1] + h[1] + h[2] + h[2]; }

                if (h.Length == 6) { return unchecked((int)0xFF000000) | int.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture); }

                if (h.Length == 8)
                {
                    int rgb = int.Parse(h.Substring(0, 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    int a = int.Parse(h.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    return (a << 24) | rgb;
                }
                throw new CliError("bad colour '" + s + "' (use #RRGGBB or #RRGGBBAA)", 2);
            }

            string[] p = t.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 3 || p.Length == 4)
            {
                int[] v = new int[4]; v[3] = 255;
                for (int i = 0; i < p.Length; i++)
                {
                    v[i] = CliArgs.ParseInt(p[i], "colour component");
                    if (v[i] < 0 || v[i] > 255) { throw new CliError("colour components must be 0..255 (got '" + s + "')", 2); }
                }
                return (v[3] << 24) | (v[0] << 16) | (v[1] << 8) | v[2];
            }
            throw new CliError("bad colour '" + s + "'. Use #RRGGBB, #RRGGBBAA, R,G,B, a name like red, or 'erase'.", 2);
        }

        internal static string Hex(int argb)
        {
            uint u = (uint)argb;
            int a = (int)(u >> 24);
            string rgb = "#" + (u & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);
            return a == 255 ? rgb : rgb + a.ToString("X2", CultureInfo.InvariantCulture);
        }

        /// <summary>顏色在調色盤裡的索引：已經有就用，沒有就佔一個空格；alpha = 0 當成擦除（索引 0）；調色盤滿了丟錯。</summary>
        internal static byte ColourIndex(Animation anim, int argb)
        {
            if (((uint)argb >> 24) == 0) { return 0; }

            int[] pal = anim.Palette;
            for (int i = 1; i < 256; i++) { if (!PaletteOps.IsFree(pal[i]) && pal[i] == argb) { return (byte)i; } }
            for (int i = 1; i < 256; i++)
            {
                if (PaletteOps.IsFree(pal[i])) { pal[i] = argb; return (byte)i; }
            }
            throw new CliError("the palette is full (255 colours). Remove unused colours first or reuse an existing colour.");
        }

        /// <summary>「顏色或 erase」→ 索引。</summary>
        internal static byte ColourOrErase(Animation anim, string s)
        {
            return IsErase(s) ? (byte)0 : ColourIndex(anim, ParseColour(s));
        }

        // --- 目標（哪一幀的哪一層）-----------------------------------------------------------------------------
        internal static int FrameIndex(Animation anim, string s, string what = "--frame")
        {
            int f = CliArgs.ParseInt(s, what);
            if (f < 0 || f >= anim.Count) { throw new CliError(what + " " + f + " is out of range (this file has " + anim.Count + " frame(s), valid 0.." + (anim.Count - 1) + ")"); }

            return f;
        }

        internal static int LayerIndex(Animation anim, string s, string what = "--layer")
        {
            int l = CliArgs.ParseInt(s, what);
            if (l < 0 || l >= anim.LayerCount) { throw new CliError(what + " " + l + " is out of range (this file has " + anim.LayerCount + " layer(s), valid 0.." + (anim.LayerCount - 1) + ")"); }

            return l;
        }

        /// <summary>要編輯的格子內容：--frame N|all（預設 0）× --layer N（預設 0）。單層的檔案「幀」本身就是那一層。</summary>
        internal static List<VoxelModel> Targets(Animation anim, string frameSpec, string layerSpec)
        {
            int layer = layerSpec == null ? 0 : LayerIndex(anim, layerSpec);
            List<VoxelModel> list = new List<VoxelModel>();
            if (frameSpec != null && frameSpec.Trim().ToLowerInvariant() == "all")
            {
                foreach (VoxelModel f in anim.Frames) { list.Add(f.IsLayered ? f.Cels[layer] : f); }
            }
            else
            {
                VoxelModel f = anim.Frames[frameSpec == null ? 0 : FrameIndex(anim, frameSpec)];
                list.Add(f.IsLayered ? f.Cels[layer] : f);
            }
            return list;
        }

        internal static void Notify(IEnumerable<VoxelModel> models)
        {
            foreach (VoxelModel m in models) { m.NotifyChanged(); }
        }

        internal static void SavePng(string path, int w, int h, int[] pixels)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }

            BitmapSource bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
            bmp.Freeze();
            PngBitmapEncoder enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (FileStream fs = File.Create(path)) { enc.Save(fs); }
        }

        private static string Full(string p) { return Path.GetFullPath(p); }

        // --- 命令登記 ------------------------------------------------------------------------------------------
        internal static void Register(List<Cmd> c)
        {
            c.Add(new Cmd("Help", "manual", "manual", "Print the full AI manual (READ THIS FIRST). Same text as AI說明書.md.", a =>
            {
                a.NeedExactly(0, "manual");
                return () => Console.Write(Program.ManualText());
            }));

            c.Add(new Cmd("Help", "help", "help [command]", "List all commands, or show one command.", a =>
            {
                string which = a.Pos.Count > 0 ? a.Pos[0].ToLowerInvariant() : null;
                return () =>
                {
                    if (which == null) { Program.PrintOverview(Console.Out); return; }

                    Cmd cmd = Program.Commands.Find(x => x.Name == which);
                    if (cmd == null) { throw new CliError("no such command '" + which + "'. Run `pstk help`.", 2); }

                    Console.WriteLine("pstk " + cmd.Usage);
                    Console.WriteLine("  " + cmd.Summary);
                };
            }));

            c.Add(new Cmd("Help", "version", "version", "Print the version.", a => () => Console.WriteLine("pstk 1.0 (PixelStacking command line; reads .pstk v1-v5, writes v5)")));

            // ---------------------------------------------------------------------------------------------
            c.Add(new Cmd("Create / inspect", "new", "new <file.pstk> <W> <H> <D> [--fps N] [--force]",
                "Create an empty model: W=length(x) H=width(y) D=height(z), each 1..128. 1 frame, 1 layer. Refuses to overwrite unless --force.", a =>
            {
                string file = a.PosStr(0, "file.pstk");
                int w = a.PosInt(1, "W"), h = a.PosInt(2, "H"), d = a.PosInt(3, "D");
                a.NeedExactly(4, "new <file.pstk> <W> <H> <D>");
                int fps = a.Int("fps", 8);
                bool force = a.Has("force");
                string outp = a.Str("out", file);
                return () =>
                {
                    if (w < 1 || w > 128 || h < 1 || h > 128 || d < 1 || d > 128) { throw new CliError("each size must be 1..128 (got " + w + "x" + h + "x" + d + ")", 2); }
                    if (File.Exists(outp) && !force) { throw new CliError("'" + outp + "' already exists (use --force to overwrite)"); }

                    Animation anim = new Animation(w, h, d) { Fps = fps };
                    SaveAndReport(anim, outp, "created " + w + "x" + h + "x" + d + " model (x length, y width, z height), 1 frame, 1 layer, fps " + anim.Fps);
                };
            }));

            c.Add(new Cmd("Create / inspect", "info", "info <file.pstk>",
                "Size, frames, fps, layers (name/visible/voxel count), palette use, bounding box, rig bones.", a =>
            {
                string file = a.PosStr(0, "file.pstk");
                a.NeedExactly(1, "info <file.pstk>");
                return () => PrintInfo(Open(file), file);
            }));

            c.Add(new Cmd("Create / inspect", "palette", "palette <file.pstk>", "List the palette colours in use (index, #RRGGBB[AA], voxel count over all frames).", a =>
            {
                string file = a.PosStr(0, "file.pstk");
                a.NeedExactly(1, "palette <file.pstk>");
                return () =>
                {
                    Animation anim = Open(file);
                    int[] usage = PaletteOps.CountUsage(anim);
                    int shown = 0;
                    for (int i = 1; i < 256; i++)
                    {
                        if (PaletteOps.IsFree(anim.Palette[i])) { continue; }

                        Console.WriteLine(i.ToString().PadLeft(3) + "  " + Hex(anim.Palette[i]).PadRight(10) + usage[i] + " voxels");
                        shown++;
                    }
                    Console.WriteLine(shown + " colour(s) defined, " + (255 - shown) + " free slot(s)");
                };
            }));

            c.Add(new Cmd("Create / inspect", "slice", "slice <file.pstk> <view> <index> [--frame N] [--layer N]",
                "Print one slice as text. view = top|bottom|front|back|left|right; '.' = empty, letters = colours (legend below). Shows how the canvas maps to x/y/z.", a =>
            {
                string file = a.PosStr(0, "file.pstk");
                ViewDirection view = ParseView(a.PosStr(1, "view"));
                int idx = a.PosInt(2, "index");
                a.NeedExactly(3, "slice <file.pstk> <view> <index>");
                string frame = a.Str("frame"), layer = a.Str("layer");
                return () =>
                {
                    Animation anim = Open(file);
                    VoxelModel m = Targets(anim, frame, layer)[0];
                    int cw = ViewMapper.CanvasWidth(view, m.Width, m.Height, m.Depth), ch = ViewMapper.CanvasHeight(view, m.Width, m.Height, m.Depth), count = ViewMapper.SliceCount(view, m.Width, m.Height, m.Depth);
                    if (idx < 0 || idx >= count) { throw new CliError("slice index " + idx + " is out of range for the " + view + " view (valid 0.." + (count - 1) + ")"); }

                    int[] right, down, toward;
                    ViewMapper.Axes(view, out right, out down, out toward);
                    Console.WriteLine(view + " view, slice " + idx + " of " + count + ": canvas " + cw + "x" + ch + "; right=" + AxisName(right) + " down=" + AxisName(down) + " toward-viewer=" + AxisName(toward));
                    byte[] cells = SliceOps.Read(m, view, idx);
                    const string sym = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
                    Dictionary<byte, char> map = new Dictionary<byte, char>();
                    StringBuilder sb = new StringBuilder();
                    for (int y = 0; y < ch; y++)
                    {
                        sb.Clear();
                        for (int x = 0; x < cw; x++)
                        {
                            byte v = cells[y * cw + x];
                            if (v == 0) { sb.Append('.'); continue; }

                            char s;
                            if (!map.TryGetValue(v, out s)) { s = map.Count < sym.Length ? sym[map.Count] : '#'; map[v] = s; }

                            sb.Append(s);
                        }
                        Console.WriteLine(sb.ToString());
                    }
                    foreach (KeyValuePair<byte, char> kv in map) { Console.WriteLine(kv.Value + " = palette " + kv.Key + " " + Hex(anim.Palette[kv.Key])); }
                    if (map.Count == 0) { Console.WriteLine("(slice is empty)"); }
                };
            }));

            c.Add(new Cmd("Create / inspect", "render", "render <file.pstk> <out.png> [--frame N] [--layer N] [--yaw 35] [--elev 30] [--scale 6] [--no-shade] [--no-trim] [--padding N] [--bg #RRGGBB] [--outline [--no-silhouette] [--no-creases] [--outline-width 1|2|3] [--outline-tone light|medium|dark]]",
                "Render one frame to a PNG (true colour + alpha). --layer renders only that layer. yaw rotates around the vertical axis (degrees), elev is the camera pitch (90 = from above, 0 = from the side).", a =>
            {
                string file = a.PosStr(0, "file.pstk"), outPng = a.PosStr(1, "out.png");
                a.NeedExactly(2, "render <file.pstk> <out.png>");
                string frameS = a.Str("frame", "0"), layerS = a.Str("layer");
                double yaw = a.Dbl("yaw", 35), elev = a.Dbl("elev", 30);
                int scale = a.Int("scale", 6), padding = a.Int("padding", 0);
                bool shade = !a.Has("no-shade"), trim = !a.Has("no-trim");
                string bg = a.Str("bg");
                OutlineOptions outline = ReadOutline(a);
                return () =>
                {
                    Animation anim = Open(file);
                    int f = FrameIndex(anim, frameS);
                    VoxelModel src = anim.Frames[f];
                    if (layerS != null) { src = anim.Frames[f].IsLayered ? anim.Frames[f].Cels[LayerIndex(anim, layerS)] : anim.Frames[f]; }

                    Animation tmp = Animation.FromFrames(new List<VoxelModel> { src }, anim.Fps);
                    int cw, chh;
                    List<int[]> px = SpriteExporter.RenderFrames(tmp, yaw, elev, scale, shade, trim, padding, outline, null, null, out cw, out chh);
                    int[] pixels = px[0];
                    if (bg != null)
                    {
                        int bgc = ParseColour(bg) | unchecked((int)0xFF000000);
                        for (int i = 0; i < pixels.Length; i++) { pixels[i] = Over(pixels[i], bgc); }
                    }
                    SavePng(outPng, cw, chh, pixels);
                    Console.WriteLine("rendered frame " + f + (layerS != null ? " layer " + layerS : "") + " -> " + Full(outPng) + " (" + cw + "x" + chh + " px)");
                };
            }));

            // ---------------------------------------------------------------------------------------------
            c.Add(new Cmd("Export", "export-png", "export-png <file.pstk> <out.png> [--dirs N] [--yaw 35] [--elev 30] [--scale 4] [--no-shade] [--no-trim] [--padding N] [--kind sheet|sequence|both] [--outline ...]",
                "Sprite sheet PNG (+ .json): rows = directions around a full turn, columns = frames. Also/or a PNG sequence folder.", a =>
            {
                string file = a.PosStr(0, "file.pstk"), outPng = a.PosStr(1, "out.png");
                a.NeedExactly(2, "export-png <file.pstk> <out.png>");
                ExportOptions o = new ExportOptions
                {
                    Path = outPng, YawDegrees = a.Dbl("yaw", 35), ElevationDegrees = a.Dbl("elev", 30), Directions = a.Int("dirs", 1),
                    Scale = a.Int("scale", 4), Shade = !a.Has("no-shade"), Trim = !a.Has("no-trim"), Padding = a.Int("padding", 0),
                    Outline = ReadOutline(a),
                };
                string kind = (a.Str("kind", "sheet") ?? "sheet").ToLowerInvariant();
                if (kind == "sheet") { o.Kind = ExportKind.Sheet; }
                else if (kind == "sequence") { o.Kind = ExportKind.Sequence; }
                else if (kind == "both") { o.Kind = ExportKind.Both; }
                else { throw new CliError("--kind must be sheet, sequence or both", 2); }
                return () =>
                {
                    Animation anim = Open(file);
                    string dir = Path.GetDirectoryName(Full(outPng));
                    if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }

                    ExportResult r = SpriteExporter.Export(anim, o, null, null);
                    Console.WriteLine("exported: cell " + r.CellWidth + "x" + r.CellHeight + " px, " + r.Rows + " direction(s) x " + r.Columns + " frame(s)");
                    if (r.SheetPath != null) { Console.WriteLine("sheet: " + r.SheetPath + " (" + r.SheetWidth + "x" + r.SheetHeight + ")"); }
                    if (r.JsonPath != null) { Console.WriteLine("json: " + r.JsonPath); }
                    if (r.SequenceDir != null) { Console.WriteLine("sequence: " + r.SequenceDir + " (" + r.SequenceFiles + " files)"); }
                };
            }));

            c.Add(new Cmd("Export", "export-gif", "export-gif <file.pstk> <out.gif> [--yaw 35] [--elev 30] [--scale 4] [--no-shade] [--no-trim] [--padding N] [--bg #RRGGBB] [--no-loop] [--outline ...]",
                "Animated GIF of all frames from a fixed angle. Transparent background unless --bg is given.", a =>
            {
                string file = a.PosStr(0, "file.pstk"), outGif = a.PosStr(1, "out.gif");
                a.NeedExactly(2, "export-gif <file.pstk> <out.gif>");
                GifOptions o = new GifOptions
                {
                    Path = outGif, YawDegrees = a.Dbl("yaw", 35), ElevationDegrees = a.Dbl("elev", 30), Scale = a.Int("scale", 4),
                    Shade = !a.Has("no-shade"), Trim = !a.Has("no-trim"), Padding = a.Int("padding", 0), Loop = !a.Has("no-loop"),
                    Outline = ReadOutline(a),
                };
                string bg = a.Str("bg");
                if (bg != null) { o.TransparentBackground = false; o.Background = ParseColour(bg) & 0xFFFFFF; }
                return () =>
                {
                    Animation anim = Open(file);
                    string dir = Path.GetDirectoryName(Full(outGif));
                    if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }

                    GifResult r = GifExporter.Export(anim, o, null, null);
                    Console.WriteLine("exported GIF: " + r.Width + "x" + r.Height + " px, " + r.Frames + " frame(s), " + (r.DelayCs * 10) + " ms per frame, " + r.Colours + " colours" + (r.Quantized ? " (quantized)" : "") + ", " + r.Bytes + " bytes");
                    Console.WriteLine("gif: " + Full(outGif));
                };
            }));

            c.Add(new Cmd("Export", "export-strip", "export-strip <file.pstk> <out.png> [--scale 4] [--no-trim] [--padding N] [--top-first]",
                "Sprite-stacking strip: every slice (top view) is one tile in a single row, one row per frame (+ .json). The game stacks and rotates the slices itself.", a =>
            {
                string file = a.PosStr(0, "file.pstk"), outPng = a.PosStr(1, "out.png");
                a.NeedExactly(2, "export-strip <file.pstk> <out.png>");
                StripOptions o = new StripOptions { Path = outPng, Scale = a.Int("scale", 4), Trim = !a.Has("no-trim"), Padding = a.Int("padding", 0), TopFirst = a.Has("top-first") };
                return () =>
                {
                    Animation anim = Open(file);
                    string dir = Path.GetDirectoryName(Full(outPng));
                    if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }

                    StripResult r = StripExporter.Export(anim, o, null, null);
                    Console.WriteLine("exported strip: tile " + r.LayerWidth + "x" + r.LayerHeight + " px, " + r.Layers + " slices x " + r.Frames + " frame(s), sheet " + r.SheetWidth + "x" + r.SheetHeight);
                    Console.WriteLine("png: " + r.PngPath);
                    Console.WriteLine("json: " + r.JsonPath);
                };
            }));
        }

        private static int Over(int fg, int bg)
        {
            uint f = (uint)fg;
            int a = (int)(f >> 24);
            if (a == 255) { return fg; }
            if (a == 0) { return bg; }

            uint b = (uint)bg;
            int r = (int)(((f >> 16) & 255) * a + ((b >> 16) & 255) * (255 - a)) / 255;
            int g = (int)(((f >> 8) & 255) * a + ((b >> 8) & 255) * (255 - a)) / 255;
            int bl = (int)((f & 255) * a + (b & 255) * (255 - a)) / 255;
            return unchecked((int)0xFF000000) | (r << 16) | (g << 8) | bl;
        }

        private static void PrintInfo(Animation anim, string file)
        {
            Console.WriteLine("file: " + file);
            Console.WriteLine("size: " + anim.Width + " x " + anim.Height + " x " + anim.Depth + "   (x = length 0.." + (anim.Width - 1) + ", y = width 0.." + (anim.Height - 1) + ", z = height 0.." + (anim.Depth - 1) + ", z up)");
            Console.WriteLine("frames: " + anim.Count + "   fps: " + anim.Fps);

            int[] usage = PaletteOps.CountUsage(anim);
            int used = 0, defined = 0;
            for (int i = 1; i < 256; i++) { if (!PaletteOps.IsFree(anim.Palette[i])) { defined++; if (usage[i] > 0) { used++; } } }
            Console.WriteLine("palette: " + defined + " colour(s) defined, " + used + " in use, " + (255 - defined) + " free slot(s)");

            Console.WriteLine("layers: " + anim.LayerCount + (anim.IsLayered ? "" : "   (single layer: frame data is the layer)"));
            for (int l = 0; l < anim.LayerCount; l++)
            {
                int total = 0;
                foreach (VoxelModel f in anim.Frames) { total += Count(f.IsLayered ? f.Cels[l] : f); }
                int f0 = Count(anim.Frames[0].IsLayered ? anim.Frames[0].Cels[l] : anim.Frames[0]);
                Console.WriteLine("  [" + l + "] \"" + anim.Layers[l].Name + "\" " + (anim.Layers[l].Visible ? "visible" : "HIDDEN") + "  voxels: frame0=" + f0 + " all-frames=" + total);
            }

            for (int f = 0; f < anim.Count; f++)
            {
                VoxelModel m = anim.Frames[f];
                int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue, maxX = -1, maxY = -1, maxZ = -1, n = 0;
                int wh = m.Width * m.Height;
                for (int i = 0; i < m.Data.Length; i++)
                {
                    if (m.Data[i] == 0) { continue; }

                    n++;
                    int z = i / wh, r = i - z * wh, y = r / m.Width, x = r - y * m.Width;
                    if (x < minX) { minX = x; } if (x > maxX) { maxX = x; }
                    if (y < minY) { minY = y; } if (y > maxY) { maxY = y; }
                    if (z < minZ) { minZ = z; } if (z > maxZ) { maxZ = z; }
                }
                if (anim.Count > 12 && f >= 3 && f < anim.Count - 1) { if (f == 3) { Console.WriteLine("  frame ...: (" + (anim.Count - 4) + " more)"); } continue; }

                Console.WriteLine("  frame " + f + ": " + n + " voxel(s)" + (n == 0 ? "" : ", bounds x " + minX + ".." + maxX + ", y " + minY + ".." + maxY + ", z " + minZ + ".." + maxZ) + (m.Parts != null ? ", has part marks" : ""));
            }

            Rig rig = anim.Rig;
            Console.WriteLine("rig: " + rig.Count + " bone(s)");
            foreach (Bone b in rig.Bones)
            {
                Bone parent = b.ParentId == 0 ? null : rig.Find(b.ParentId);
                Console.WriteLine("  id " + b.Id + " \"" + b.Name + "\" axis " + b.Axis + " pivot (" + Num(b.PivotX) + ", " + Num(b.PivotY) + ", " + Num(b.PivotZ) + ") parent " + (parent == null ? "(body)" : "\"" + parent.Name + "\""));
            }
        }

        internal static string Num(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }

        private static int Count(VoxelModel m)
        {
            int n = 0;
            byte[] d = m.Data;
            for (int i = 0; i < d.Length; i++) { if (d[i] != 0) { n++; } }
            return n;
        }
    }
}
