using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PixelStacking.Data;
using PixelStacking.Render;

namespace PixelStacking.Cli
{
    /// <summary>改模型的命令：畫體素、匯入圖片、膨脹、幀、圖層、3D 魔術棒。</summary>
    internal static class CmdsEdit
    {
        private static string Out(CliArgs a, string file) { return a.Str("out", file); }

        // --- 畫圖共用 ------------------------------------------------------------------------------------------
        /// <summary>在 [x0..x1]×[y0..y1]×[z0..z1]（會裁到模型範圍內）裡，inside 為真的格子寫成 idx。回傳改變的體素數；整個形狀都在模型外面丟錯。</summary>
        private static int Paint(List<VoxelModel> targets, int x0, int y0, int z0, int x1, int y1, int z1, Func<int, int, int, bool> inside, byte idx)
        {
            VoxelModel first = targets[0];
            int w = first.Width, h = first.Height, d = first.Depth, wh = w * h;
            int ax = Math.Max(0, Math.Min(x0, x1)), bx = Math.Min(w - 1, Math.Max(x0, x1));
            int ay = Math.Max(0, Math.Min(y0, y1)), by = Math.Min(h - 1, Math.Max(y0, y1));
            int az = Math.Max(0, Math.Min(z0, z1)), bz = Math.Min(d - 1, Math.Max(z0, z1));
            if (ax > bx || ay > by || az > bz) { throw new CliError("the shape is entirely outside the model (size " + w + "x" + h + "x" + d + ", coordinates are 0-based)"); }

            int changed = 0;
            foreach (VoxelModel m in targets)
            {
                for (int z = az; z <= bz; z++)
                {
                    for (int y = ay; y <= by; y++)
                    {
                        for (int x = ax; x <= bx; x++)
                        {
                            if (!inside(x, y, z)) { continue; }

                            int i = z * wh + y * w + x;
                            if (m.Data[i] != idx) { m.Data[i] = idx; changed++; }
                        }
                    }
                }
            }
            Cmds.Notify(targets);
            return changed;
        }

        private static string Where(CliArgs a) { return "frame " + a.Str("frame", "0") + ", layer " + a.Str("layer", "0"); }

        // --- 登記 -----------------------------------------------------------------------------------------------
        internal static void Register(List<Cmd> c)
        {
            const string target = " [--frame N|all] [--layer N]";

            c.Add(new Cmd("Draw voxels", "set", "set <file> <x> <y> <z> <colour>" + target, "Set one voxel. colour = #RRGGBB | #RRGGBBAA | R,G,B | red.. | erase. New colours are added to the palette automatically.", a =>
            {
                string file = a.PosStr(0, "file");
                int x = a.PosInt(1, "x"), y = a.PosInt(2, "y"), z = a.PosInt(3, "z");
                string col = a.PosStr(4, "colour");
                a.NeedExactly(5, "set <file> <x> <y> <z> <colour>");
                string fr = a.Str("frame"), ly = a.Str("layer"), outp = Out(a, file);
                return () =>
                {
                    Animation anim = Cmds.Open(file);
                    List<VoxelModel> t = Cmds.Targets(anim, fr, ly);
                    byte idx = Cmds.ColourOrErase(anim, col);
                    if (!t[0].InBounds(x, y, z)) { throw new CliError("(" + x + "," + y + "," + z + ") is outside the model (size " + t[0].Width + "x" + t[0].Height + "x" + t[0].Depth + ")"); }

                    int n = Paint(t, x, y, z, x, y, z, (px, py, pz) => true, idx);
                    Cmds.SaveAndReport(anim, outp, "set voxel (" + x + "," + y + "," + z + ") = " + col + " on " + t.Count + " target(s), " + n + " changed");
                };
            }));

            c.Add(new Cmd("Draw voxels", "voxels", "voxels <file> <list.txt|->" + target, "Bulk set. Each line of the list: 'x y z colour' (spaces or commas; blank lines and lines starting with // are ignored). '-' reads stdin. Fast way to build a model from a script.", a =>
            {
                string file = a.PosStr(0, "file"), list = a.PosStr(1, "list");
                a.NeedExactly(2, "voxels <file> <list.txt|->");
                string fr = a.Str("frame"), ly = a.Str("layer"), outp = Out(a, file);
                return () =>
                {
                    Animation anim = Cmds.Open(file);
                    List<VoxelModel> t = Cmds.Targets(anim, fr, ly);
                    string[] lines = (list == "-" ? Console.In.ReadToEnd() : File.ReadAllText(list)).Split('\n');
                    int n = 0, changed = 0, lineNo = 0, outside = 0;
                    Dictionary<string, byte> cache = new Dictionary<string, byte>();
                    foreach (string raw in lines)
                    {
                        lineNo++;
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith(";", StringComparison.Ordinal)) { continue; }

                        string[] p = line.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
                        int x, y, z;
                        if (p.Length < 4 || !int.TryParse(p[0], out x) || !int.TryParse(p[1], out y) || !int.TryParse(p[2], out z)) { throw new CliError("line " + lineNo + ": expected 'x y z colour', got '" + line + "'"); }

                        string col = p.Length == 4 ? p[3] : string.Join(",", p.Skip(3));
                        byte idx;
                        if (!cache.TryGetValue(col, out idx)) { idx = Cmds.ColourOrErase(anim, col); cache[col] = idx; }

                        n++;
                        if (!t[0].InBounds(x, y, z)) { outside++; continue; }

                        changed += Paint(t, x, y, z, x, y, z, (px, py, pz) => true, idx);
                    }
                    Cmds.SaveAndReport(anim, outp, n + " line(s) read, " + changed + " voxel(s) changed" + (outside > 0 ? ", " + outside + " outside the model were skipped" : ""));
                };
            }));

            c.Add(new Cmd("Draw voxels", "box", "box <file> <x0> <y0> <z0> <x1> <y1> <z1> <colour> [--hollow]" + target, "Fill a box (inclusive corners, clipped to the model). --hollow = only the outer shell. colour 'erase' clears the box.", a =>
            {
                string file = a.PosStr(0, "file");
                int x0 = a.PosInt(1, "x0"), y0 = a.PosInt(2, "y0"), z0 = a.PosInt(3, "z0"), x1 = a.PosInt(4, "x1"), y1 = a.PosInt(5, "y1"), z1 = a.PosInt(6, "z1");
                string col = a.PosStr(7, "colour");
                a.NeedExactly(8, "box <file> <x0> <y0> <z0> <x1> <y1> <z1> <colour>");
                bool hollow = a.Has("hollow");
                string fr = a.Str("frame"), ly = a.Str("layer"), outp = Out(a, file);
                return () =>
                {
                    Animation anim = Cmds.Open(file);
                    List<VoxelModel> t = Cmds.Targets(anim, fr, ly);
                    byte idx = Cmds.ColourOrErase(anim, col);
                    int lx = Math.Min(x0, x1), hx = Math.Max(x0, x1), ly0 = Math.Min(y0, y1), hy = Math.Max(y0, y1), lz = Math.Min(z0, z1), hz = Math.Max(z0, z1);
                    int n = Paint(t, x0, y0, z0, x1, y1, z1, (x, y, z) => !hollow || x == lx || x == hx || y == ly0 || y == hy || z == lz || z == hz, idx);
                    Cmds.SaveAndReport(anim, outp, "box " + col + (hollow ? " (hollow)" : "") + ": " + n + " voxel(s) changed on " + t.Count + " target(s)");
                };
            }));

            c.Add(new Cmd("Draw voxels", "sphere", "sphere <file> <cx> <cy> <cz> <radius> <colour> [--hollow]" + target, "Fill a sphere around a centre voxel (radius in voxels). --hollow = 1-voxel shell.", a =>
            {
                string file = a.PosStr(0, "file");
                int cx = a.PosInt(1, "cx"), cy = a.PosInt(2, "cy"), cz = a.PosInt(3, "cz");
                double r = a.PosDbl(4, "radius");
                string col = a.PosStr(5, "colour");
                a.NeedExactly(6, "sphere <file> <cx> <cy> <cz> <radius> <colour>");
                bool hollow = a.Has("hollow");
                string fr = a.Str("frame"), ly = a.Str("layer"), outp = Out(a, file);
                return () =>
                {
                    if (r < 0 || r > 200) { throw new CliError("radius must be 0..200", 2); }

                    Animation anim = Cmds.Open(file);
                    List<VoxelModel> t = Cmds.Targets(anim, fr, ly);
                    byte idx = Cmds.ColourOrErase(anim, col);
                    int ri = (int)Math.Ceiling(r);
                    double outer = r * r + 0.5, inner = (r - 1) * (r - 1) + 0.5;
                    int n = Paint(t, cx - ri, cy - ri, cz - ri, cx + ri, cy + ri, cz + ri, (x, y, z) =>
                    {
                        double d2 = (x - cx) * (x - cx) + (y - cy) * (y - cy) + (z - cz) * (z - cz);
                        return d2 <= outer && (!hollow || r < 1 || d2 > inner);
                    }, idx);
                    Cmds.SaveAndReport(anim, outp, "sphere r=" + r + " " + col + ": " + n + " voxel(s) changed on " + t.Count + " target(s)");
                };
            }));

            c.Add(new Cmd("Draw voxels", "line", "line <file> <x0> <y0> <z0> <x1> <y1> <z1> <colour>" + target, "Draw a 3D line (1 voxel thick) between two voxels.", a =>
            {
                string file = a.PosStr(0, "file");
                int x0 = a.PosInt(1, "x0"), y0 = a.PosInt(2, "y0"), z0 = a.PosInt(3, "z0"), x1 = a.PosInt(4, "x1"), y1 = a.PosInt(5, "y1"), z1 = a.PosInt(6, "z1");
                string col = a.PosStr(7, "colour");
                a.NeedExactly(8, "line <file> <x0> <y0> <z0> <x1> <y1> <z1> <colour>");
                string fr = a.Str("frame"), ly = a.Str("layer"), outp = Out(a, file);
                return () =>
                {
                    Animation anim = Cmds.Open(file);
                    List<VoxelModel> t = Cmds.Targets(anim, fr, ly);
                    byte idx = Cmds.ColourOrErase(anim, col);
                    int steps = Math.Max(Math.Abs(x1 - x0), Math.Max(Math.Abs(y1 - y0), Math.Abs(z1 - z0)));
                    int n = 0;
                    for (int s = 0; s <= steps; s++)
                    {
                        double f = steps == 0 ? 0 : (double)s / steps;
                        int x = (int)Math.Round(x0 + (x1 - x0) * f), y = (int)Math.Round(y0 + (y1 - y0) * f), z = (int)Math.Round(z0 + (z1 - z0) * f);
                        if (!t[0].InBounds(x, y, z)) { continue; }

                        n += Paint(t, x, y, z, x, y, z, (px, py, pz) => true, idx);
                    }
                    Cmds.SaveAndReport(anim, outp, "line " + col + ": " + n + " voxel(s) changed on " + t.Count + " target(s)");
                };
            }));

            c.Add(new Cmd("Draw voxels", "clear", "clear <file>" + target, "Erase everything in the target layer of the target frame(s).", a =>
            {
                string file = a.PosStr(0, "file");
                a.NeedExactly(1, "clear <file>");
                string fr = a.Str("frame"), ly = a.Str("layer"), outp = Out(a, file);
                return () =>
                {
                    Animation anim = Cmds.Open(file);
                    List<VoxelModel> t = Cmds.Targets(anim, fr, ly);
                    int n = 0;
                    foreach (VoxelModel m in t) { foreach (byte b in m.Data) { if (b != 0) { n++; } } Array.Clear(m.Data, 0, m.Data.Length); }
                    Cmds.Notify(t);
                    Cmds.SaveAndReport(anim, outp, "cleared " + n + " voxel(s) on " + t.Count + " target(s)");
                };
            }));

            c.Add(new Cmd("Draw voxels", "palette-set", "palette-set <file> <index> <colour>", "Change palette slot <index> (1..255) to a new colour: every voxel using it changes colour. Use `pstk palette` to see the slots.", a =>
            {
                string file = a.PosStr(0, "file");
                int index = a.PosInt(1, "index");
                string col = a.PosStr(2, "colour");
                a.NeedExactly(3, "palette-set <file> <index> <colour>");
                string outp = Out(a, file);
                return () =>
                {
                    Animation anim = Cmds.Open(file);
                    int argb = Cmds.ParseColour(col);
                    if (PaletteOps.ReplaceColour(anim, index, argb) == null) { throw new CliError("cannot change slot " + index + " (it must be a colour in use, 1..255, and the new colour must be non-transparent and different)"); }

                    foreach (VoxelModel f in anim.Frames) { f.NotifyChanged(); }
                    Cmds.SaveAndReport(anim, outp, "palette slot " + index + " is now " + Cmds.Hex(argb));
                };
            }));

            // --- 圖片 --------------------------------------------------------------------------------------------
            c.Add(new Cmd("Images", "import-image", "import-image <file> <image> [--view front] [--slice 0] [--fit contain|center|topleft|stretch] [--strip-width N] [--overwrite-transparent]" + target,
                "Paint an image onto slice(s) of a view. With --strip-width N the image is cut into tiles N px wide and tile k goes to slice+k (sprite-stacking strips). Transparent pixels keep what is there unless --overwrite-transparent.", a =>
            {
                string file = a.PosStr(0, "file"), image = a.PosStr(1, "image");
                a.NeedExactly(2, "import-image <file> <image>");
                ViewDirection view = Cmds.ParseView(a.Str("view", "front"));
                int slice = a.Int("slice", 0), strip = a.Int("strip-width", 0);
                ImageFit fit = ParseFit(a.Str("fit", "contain"));
                bool keepT = !a.Has("overwrite-transparent");
                string fr = a.Str("frame"), ly = a.Str("layer"), outp = Out(a, file);
                return () =>
                {
                    Animation anim = Cmds.Open(file);
                    VoxelModel m = Cmds.Targets(anim, fr, ly)[0];
                    int cw = ViewMapper.CanvasWidth(view, m.Width, m.Height, m.Depth), ch = ViewMapper.CanvasHeight(view, m.Width, m.Height, m.Depth), count = ViewMapper.SliceCount(view, m.Width, m.Height, m.Depth);
                    if (slice < 0 || slice >= count) { throw new CliError("--slice " + slice + " is out of range for the " + view + " view (valid 0.." + (count - 1) + ")"); }

                    LoadedImage im = LoadImage(image);
                    List<int[]> layers = new List<int[]>();
                    if (strip > 0)
                    {
                        foreach (int[] part in ImageImport.SplitStrip(im.Pixels, im.Width, im.Height, strip, im.Height)) { layers.Add(ImageImport.Fit(part, strip, im.Height, cw, ch, fit)); }
                    }
                    else { layers.Add(ImageImport.Fit(im.Pixels, im.Width, im.Height, cw, ch, fit)); }

                    int room = count - slice, dropped = Math.Max(0, layers.Count - room);
                    if (dropped > 0) { layers.RemoveRange(room, dropped); }

                    ImageImport.MapStats stats;
                    byte[][] idx = ImageImport.MapToPalette(anim, layers, out stats);
                    int written = 0;
                    for (int k = 0; k < idx.Length; k++) { written += SliceOps.Write(m, view, slice + k, idx[k], keepT).Count; }
                    m.NotifyChanged();
                    Cmds.SaveAndReport(anim, outp, "imported " + layers.Count + " slice(s) into the " + view + " view starting at slice " + slice + ": " + written + " voxel(s) changed"
                        + (stats.Added > 0 ? ", " + stats.Added + " new palette colour(s)" : "") + (stats.Quantized ? ", colours were quantized to fit the palette" : "") + (dropped > 0 ? ", " + dropped + " tile(s) did not fit and were dropped" : ""));
                };
            }));

            c.Add(new Cmd("Images", "inflate", "inflate <file> <image> [--view front] [--fit contain] [--threshold 128] [--profile round|smooth|cone|flat] [--side both|front|back] [--depth auto|N] [--min-depth 2] [--radius 0] [--smooth 1] [--keep]" + target,
                "Turn a 2D picture into a 3D model: the alpha channel is the silhouette, every pixel becomes a column whose length grows with its distance from the outline. --depth N = thickest N voxels (default auto). --keep = do not clear the layer first.", a =>
            {
                string file = a.PosStr(0, "file"), image = a.PosStr(1, "image");
                a.NeedExactly(2, "inflate <file> <image>");
                InflateOptions o = new InflateOptions
                {
                    View = Cmds.ParseView(a.Str("view", "front")), Fit = ParseFit(a.Str("fit", "contain")), AlphaThreshold = a.Int("threshold", 128),
                    MinDepth = a.Int("min-depth", 2), Radius = a.Dbl("radius", 0), Smooth = a.Int("smooth", 1), Clear = !a.Has("keep"),
                };
                switch ((a.Str("profile", "round") ?? "").ToLowerInvariant())
                {
                    case "round": o.Profile = InflateProfile.Round; break;
                    case "smooth": o.Profile = InflateProfile.Smooth; break;
                    case "cone": o.Profile = InflateProfile.Cone; break;
                    case "flat": o.Profile = InflateProfile.Flat; break;
                    default: throw new CliError("--profile must be round, smooth, cone or flat", 2);
                }
                switch ((a.Str("side", "both") ?? "").ToLowerInvariant())
                {
                    case "both": o.Side = InflateSide.Both; break;
                    case "front": o.Side = InflateSide.Front; break;
                    case "back": o.Side = InflateSide.Back; break;
                    default: throw new CliError("--side must be both, front or back", 2);
                }
                string depth = (a.Str("depth", "auto") ?? "auto").ToLowerInvariant();
                if (depth == "auto") { o.AutoDepth = true; }
                else { o.AutoDepth = false; o.MaxDepth = CliArgs.ParseInt(depth, "--depth"); }
                string fr = a.Str("frame"), ly = a.Str("layer"), outp = Out(a, file);
                return () =>
                {
                    Animation anim = Cmds.Open(file);
                    VoxelModel m = Cmds.Targets(anim, fr, ly)[0];
                    LoadedImage im = LoadImage(image);
                    int cw = ViewMapper.CanvasWidth(o.View, m.Width, m.Height, m.Depth), ch = ViewMapper.CanvasHeight(o.View, m.Width, m.Height, m.Depth), count = ViewMapper.SliceCount(o.View, m.Width, m.Height, m.Depth);
                    int[] canvas = Inflate.PrepareCanvas(im, cw, ch, o.Fit, o.AlphaThreshold);
                    ImageImport.MapStats stats;
                    byte[] indices = ImageImport.MapToPalette(anim, new List<int[]> { canvas }, out stats)[0];
                    int[] thickness = Inflate.Thickness(indices, cw, ch, count, o);
                    int written = Inflate.Fill(m, o.View, indices, thickness, o.Side, o.Clear);
                    if (written == 0) { throw new CliError("nothing was written: the image is fully transparent or below --threshold " + o.AlphaThreshold); }

                    m.NotifyChanged();
                    Cmds.SaveAndReport(anim, outp, "inflated " + im.Width + "x" + im.Height + " image into a " + o.View + "-view model: " + written + " voxel(s)" + (stats.Added > 0 ? ", " + stats.Added + " new palette colour(s)" : "") + (stats.Quantized ? ", colours were quantized" : ""));
                };
            }));

            // --- 幀 ------------------------------------------------------------------------------------------------
            c.Add(new Cmd("Frames", "frame-add", "frame-add <file> [--copy N] [--at I]", "Add a frame at the end (or --at I): empty, or a copy of frame N (including layers and part marks).", a =>
            {
                string file = a.PosStr(0, "file");
                a.NeedExactly(1, "frame-add <file>");
                string copy = a.Str("copy"), at = a.Str("at"), outp = Out(a, file);
                return () =>
                {
                    Animation anim = Cmds.Open(file);
                    int pos = at == null ? anim.Count : CliArgs.ParseInt(at, "--at");
                    if (pos < 0 || pos > anim.Count) { throw new CliError("--at must be 0.." + anim.Count); }

                    VoxelModel f = copy == null ? anim.NewFrame() : anim.Frames[Cmds.FrameIndex(anim, copy, "--copy")].Clone();
                    anim.Insert(pos, f);
                    Cmds.SaveAndReport(anim, outp, "added frame " + pos + " (" + (copy == null ? "empty" : "copy of frame " + copy) + "); now " + anim.Count + " frame(s)");
                };
            }));

            c.Add(new Cmd("Frames", "frame-remove", "frame-remove <file> <N>", "Delete frame N (a file always keeps at least one frame).", a =>
            {
                string file = a.PosStr(0, "file");
                int n = a.PosInt(1, "N");
                a.NeedExactly(2, "frame-remove <file> <N>");
                string outp = Out(a, file);
                return () =>
                {
                    Animation anim = Cmds.Open(file);
                    if (anim.Count <= 1) { throw new CliError("cannot remove the only frame"); }

                    VoxelModel f = anim.Frames[Cmds.FrameIndex(anim, n.ToString(), "N")];
                    anim.Remove(f);
                    Cmds.SaveAndReport(anim, outp, "removed frame " + n + "; now " + anim.Count + " frame(s)");
                };
            }));

            c.Add(new Cmd("Frames", "frame-move", "frame-move <file> <from> <to>", "Move frame <from> so that it ends up at position <to>.", a =>
            {
                string file = a.PosStr(0, "file");
                int from = a.PosInt(1, "from"), to = a.PosInt(2, "to");
                a.NeedExactly(3, "frame-move <file> <from> <to>");
                string outp = Out(a, file);
                return () =>
                {
                    Animation anim = Cmds.Open(file);
                    Cmds.FrameIndex(anim, from.ToString(), "from"); Cmds.FrameIndex(anim, to.ToString(), "to");
                    anim.Move(from, to);
                    Cmds.SaveAndReport(anim, outp, "moved frame " + from + " to " + to);
                };
            }));

            c.Add(new Cmd("Frames", "fps", "fps <file> <N>", "Set the playback speed, 1..30 frames per second.", a =>
            {
                string file = a.PosStr(0, "file");
                int n = a.PosInt(1, "N");
                a.NeedExactly(2, "fps <file> <N>");
                string outp = Out(a, file);
                return () =>
                {
                    if (n < Animation.MinFps || n > Animation.MaxFps) { throw new CliError("fps must be " + Animation.MinFps + ".." + Animation.MaxFps, 2); }

                    Animation anim = Cmds.Open(file);
                    anim.Fps = n;
                    Cmds.SaveAndReport(anim, outp, "fps = " + n);
                };
            }));

            // --- 圖層 ----------------------------------------------------------------------------------------------
            c.Add(new Cmd("Layers", "layer-add", "layer-add <file> [name] [--at I]", "Add an empty layer at position I (default: on top). Upper layers cover lower ones. Layer 0 is the bottom.", a =>
            {
                string file = a.PosStr(0, "file");
                string name = a.Pos.Count > 1 ? a.Pos[1] : null;
                if (a.Pos.Count > 2) { throw new CliError("too many arguments. usage: layer-add <file> [name]", 2); }

                string at = a.Str("at"), outp = Out(a, file);
                return () =>
                {
                    Animation anim = Cmds.Open(file);
                    if (anim.LayerCount >= Animation.MaxLayers) { throw new CliError("layer limit reached (" + Animation.MaxLayers + ")"); }

                    int pos = at == null ? anim.LayerCount : CliArgs.ParseInt(at, "--at");
                    if (pos < 0 || pos > anim.LayerCount) { throw new CliError("--at must be 0.." + anim.LayerCount); }

                    int idx = anim.AddLayer(pos, name ?? "Layer " + (anim.LayerCount + 1));
                    Cmds.SaveAndReport(anim, outp, "added layer [" + idx + "] \"" + anim.Layers[idx].Name + "\"; now " + anim.LayerCount + " layer(s)");
                };
            }));

            c.Add(new Cmd("Layers", "layer-remove", "layer-remove <file> <index>", "Delete a layer and its content in every frame (not the last layer).", a =>
            {
                string file = a.PosStr(0, "file");
                int i = a.PosInt(1, "index");
                a.NeedExactly(2, "layer-remove <file> <index>");
                string outp = Out(a, file);
                return () =>
                {
                    Animation anim = Cmds.Open(file);
                    Cmds.LayerIndex(anim, i.ToString(), "index");
                    if (anim.LayerCount <= 1) { throw new CliError("cannot remove the only layer"); }

                    anim.RemoveLayer(i);
                    Cmds.SaveAndReport(anim, outp, "removed layer " + i + "; now " + anim.LayerCount + " layer(s)");
                };
            }));

            c.Add(new Cmd("Layers", "layer-rename", "layer-rename <file> <index> <name>", "Rename a layer.", a =>
            {
                string file = a.PosStr(0, "file");
                int i = a.PosInt(1, "index");
                string name = a.PosStr(2, "name");
                a.NeedExactly(3, "layer-rename <file> <index> <name>");
                string outp = Out(a, file);
                return () =>
                {
                    Animation anim = Cmds.Open(file);
                    Cmds.LayerIndex(anim, i.ToString(), "index");
                    anim.RenameLayer(i, name);
                    Cmds.SaveAndReport(anim, outp, "layer " + i + " is now named \"" + name + "\"");
                };
            }));

            c.Add(new Cmd("Layers", "layer-move", "layer-move <file> <from> <to>", "Change the stacking order: move layer <from> so it ends up at position <to>.", a =>
            {
                string file = a.PosStr(0, "file");
                int from = a.PosInt(1, "from"), to = a.PosInt(2, "to");
                a.NeedExactly(3, "layer-move <file> <from> <to>");
                string outp = Out(a, file);
                return () =>
                {
                    Animation anim = Cmds.Open(file);
                    Cmds.LayerIndex(anim, from.ToString(), "from"); Cmds.LayerIndex(anim, to.ToString(), "to");
                    anim.MoveLayer(from, to);
                    Cmds.SaveAndReport(anim, outp, "moved layer " + from + " to " + to);
                };
            }));

            c.Add(new Cmd("Layers", "layer-dup", "layer-dup <file> <index> [name]", "Duplicate a layer (the copy goes right above it).", a =>
            {
                string file = a.PosStr(0, "file");
                int i = a.PosInt(1, "index");
                string name = a.Pos.Count > 2 ? a.Pos[2] : null;
                string outp = Out(a, file);
                return () =>
                {
                    Animation anim = Cmds.Open(file);
                    Cmds.LayerIndex(anim, i.ToString(), "index");
                    if (anim.LayerCount >= Animation.MaxLayers) { throw new CliError("layer limit reached (" + Animation.MaxLayers + ")"); }

                    int idx = anim.DuplicateLayer(i, name ?? anim.Layers[i].Name + " copy");
                    Cmds.SaveAndReport(anim, outp, "duplicated layer " + i + " as [" + idx + "] \"" + anim.Layers[idx].Name + "\"");
                };
            }));

            c.Add(new Cmd("Layers", "layer-merge", "layer-merge <file> <index>", "Merge layer <index> down into the layer below it (index >= 1). The upper layer disappears; its voxels cover the lower ones.", a =>
            {
                string file = a.PosStr(0, "file");
                int i = a.PosInt(1, "index");
                a.NeedExactly(2, "layer-merge <file> <index>");
                string outp = Out(a, file);
                return () =>
                {
                    Animation anim = Cmds.Open(file);
                    Cmds.LayerIndex(anim, i.ToString(), "index");
                    if (i < 1) { throw new CliError("layer 0 is the bottom layer: there is nothing below it to merge into"); }

                    anim.MergeDown(i);
                    Cmds.SaveAndReport(anim, outp, "merged layer " + i + " into layer " + (i - 1) + "; now " + anim.LayerCount + " layer(s)");
                };
            }));

            c.Add(new Cmd("Layers", "layer-flatten", "layer-flatten <file>", "Merge all visible layers into one (hidden layers are dropped). Do this before building a rig.", a =>
            {
                string file = a.PosStr(0, "file");
                a.NeedExactly(1, "layer-flatten <file>");
                string outp = Out(a, file);
                return () =>
                {
                    Animation anim = Cmds.Open(file);
                    anim.Flatten();
                    Cmds.SaveAndReport(anim, outp, "flattened to " + anim.LayerCount + " layer");
                };
            }));

            c.Add(new Cmd("Layers", "layer-visible", "layer-visible <file> <index> <on|off>", "Show or hide a layer (hidden layers are not drawn, rendered or exported).", a =>
            {
                string file = a.PosStr(0, "file");
                int i = a.PosInt(1, "index");
                string v = a.PosStr(2, "on|off").ToLowerInvariant();
                a.NeedExactly(3, "layer-visible <file> <index> <on|off>");
                if (v != "on" && v != "off") { throw new CliError("last argument must be on or off", 2); }

                string outp = Out(a, file);
                return () =>
                {
                    Animation anim = Cmds.Open(file);
                    Cmds.LayerIndex(anim, i.ToString(), "index");
                    anim.SetLayerVisible(i, v == "on");
                    Cmds.SaveAndReport(anim, outp, "layer " + i + " is now " + (v == "on" ? "visible" : "hidden"));
                };
            }));

            c.Add(new Cmd("Layers", "layer-import", "layer-import <file> <other.pstk> [--at I] [--name N]", "Import another .pstk as a new layer (a component such as a hat). Colours are merged into this palette; a different size is centred. Then move it with layer-shift and merge with layer-merge.", a =>
            {
                string file = a.PosStr(0, "file"), other = a.PosStr(1, "other");
                a.NeedExactly(2, "layer-import <file> <other.pstk>");
                string at = a.Str("at"), name = a.Str("name"), outp = Out(a, file);
                return () =>
                {
                    Animation anim = Cmds.Open(file), src = Cmds.Open(other);
                    if (anim.LayerCount >= Animation.MaxLayers) { throw new CliError("layer limit reached (" + Animation.MaxLayers + ")"); }

                    int pos = at == null ? anim.LayerCount : CliArgs.ParseInt(at, "--at");
                    if (pos < 0 || pos > anim.LayerCount) { throw new CliError("--at must be 0.." + anim.LayerCount); }

                    ImageImport.MapStats stats;
                    List<byte[]> data = LayerImport.FromAnimation(anim, src, out stats);
                    int idx = anim.AddLayerWithData(pos, name ?? Path.GetFileNameWithoutExtension(other), data);
                    Cmds.SaveAndReport(anim, outp, "imported \"" + other + "\" as layer [" + idx + "] \"" + anim.Layers[idx].Name + "\"" + (stats.Added > 0 ? ", " + stats.Added + " new palette colour(s)" : "") + (stats.Quantized ? ", colours were quantized" : "")
                        + (src.Width != anim.Width || src.Height != anim.Height || src.Depth != anim.Depth ? " (size " + src.Width + "x" + src.Height + "x" + src.Depth + " centred)" : ""));
                };
            }));

            c.Add(new Cmd("Layers", "layer-shift", "layer-shift <file> <layer> <dx> <dy> <dz> [--frame N|all]", "Move everything in one layer by (dx,dy,dz) voxels (+x right, +y toward the back/width, +z up). Default: frame 0 only; --frame all moves it in every frame. Voxels pushed outside the model are lost.", a =>
            {
                string file = a.PosStr(0, "file");
                int layer = a.PosInt(1, "layer"), dx = a.PosInt(2, "dx"), dy = a.PosInt(3, "dy"), dz = a.PosInt(4, "dz");
                a.NeedExactly(5, "layer-shift <file> <layer> <dx> <dy> <dz>");
                string fr = a.Str("frame", "0"), outp = Out(a, file);
                return () =>
                {
                    Animation anim = Cmds.Open(file);
                    List<VoxelModel> t = Cmds.Targets(anim, fr, layer.ToString());
                    int moved = 0, clipped = 0;
                    foreach (VoxelModel m in t)
                    {
                        VoxelMove mv = VoxelOps.Shift(m.Data, m.Width, m.Height, m.Depth, null, dx, dy, dz, true);
                        if (mv == null) { continue; }

                        moved += mv.Moved; clipped += mv.Clipped;
                        m.NotifyChanged();
                    }
                    Cmds.SaveAndReport(anim, outp, "shifted layer " + layer + " by (" + dx + "," + dy + "," + dz + "): " + moved + " voxel(s) moved on " + t.Count + " frame(s)" + (clipped > 0 ? ", " + clipped + " fell outside the model and were dropped" : ""));
                };
            }));

            // --- 3D 魔術棒 ------------------------------------------------------------------------------------------
            c.Add(new Cmd("3D magic wand", "wand", "wand <file> <x> <y> <z> [--same-colour] [--diagonal] [--move dx,dy,dz] [--delete] [--to-layer [--layer-name N]] [--frame N] [--layer N]",
                "Select the whole 3D-connected block that contains voxel (x,y,z). Alone it only reports the size/bounds. --move shifts the block (fails if it would leave the model); --delete erases it; --to-layer first cuts it into a NEW layer above (this frame only) and then --move moves it there. --same-colour = only that colour; --diagonal = corner/edge neighbours count as connected.", a =>
            {
                string file = a.PosStr(0, "file");
                int x = a.PosInt(1, "x"), y = a.PosInt(2, "y"), z = a.PosInt(3, "z");
                a.NeedExactly(4, "wand <file> <x> <y> <z>");
                bool same = a.Has("same-colour"), diag = a.Has("diagonal"), del = a.Has("delete"), toLayer = a.Has("to-layer");
                string mv = a.Str("move"), layerName = a.Str("layer-name", "part");
                int[] shift = mv == null ? null : CliArgs.ParseInts(mv, 3, "--move");
                string fr = a.Str("frame", "0"), ly = a.Str("layer"), outp = Out(a, file);
                if (del && (toLayer || shift != null)) { throw new CliError("--delete cannot be combined with --move / --to-layer", 2); }

                return () =>
                {
                    Animation anim = Cmds.Open(file);
                    if (fr.ToLowerInvariant() == "all") { throw new CliError("wand works on one frame at a time (use --frame N)", 2); }

                    int f = Cmds.FrameIndex(anim, fr);
                    int layer = ly == null ? 0 : Cmds.LayerIndex(anim, ly);
                    VoxelModel src = Cmds.Targets(anim, fr, ly)[0];
                    if (!src.InBounds(x, y, z)) { throw new CliError("(" + x + "," + y + "," + z + ") is outside the model"); }

                    int seed = z * src.Width * src.Height + y * src.Width + x;
                    bool[] sel = VoxelOps.Component(src.Data, src.Width, src.Height, src.Depth, seed, same, diag ? WandConnectivity.Corners : WandConnectivity.Faces);
                    if (sel == null) { throw new CliError("there is no voxel at (" + x + "," + y + "," + z + ") in " + Where2(f, layer) + " (the wand needs a coloured voxel)"); }

                    int n = VoxelOps.Count(sel);
                    int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue, maxX = -1, maxY = -1, maxZ = -1, wh = src.Width * src.Height;
                    for (int i = 0; i < sel.Length; i++)
                    {
                        if (!sel[i]) { continue; }

                        int zz = i / wh, r = i - zz * wh, yy = r / src.Width, xx = r - yy * src.Width;
                        if (xx < minX) { minX = xx; } if (xx > maxX) { maxX = xx; }
                        if (yy < minY) { minY = yy; } if (yy > maxY) { maxY = yy; }
                        if (zz < minZ) { minZ = zz; } if (zz > maxZ) { maxZ = zz; }
                    }
                    string box = "bounds x " + minX + ".." + maxX + ", y " + minY + ".." + maxY + ", z " + minZ + ".." + maxZ;

                    if (!del && !toLayer && shift == null)
                    {
                        Console.WriteLine("selected " + n + " voxel(s) in " + Where2(f, layer) + ": " + box + " (nothing changed, file not saved)");
                        return;
                    }

                    string msg;
                    if (del)
                    {
                        VoxelOps.Erase(src.Data, sel);
                        src.NotifyChanged();
                        msg = "deleted " + n + " voxel(s) (" + box + ")";
                    }
                    else
                    {
                        VoxelModel dest = src;
                        if (toLayer)
                        {
                            if (anim.LayerCount >= Animation.MaxLayers) { throw new CliError("layer limit reached (" + Animation.MaxLayers + ")"); }

                            byte[] extracted = VoxelOps.Extract(src.Data, sel);
                            VoxelOps.Erase(src.Data, sel);
                            List<byte[]> data = new List<byte[]>();
                            for (int i = 0; i < anim.Count; i++) { data.Add(i == f ? extracted : null); }

                            int newIndex = anim.AddLayerWithData(layer + 1, layerName, data);
                            dest = anim.Frames[f].Cels[newIndex];
                            layer = newIndex;
                        }
                        string movedText = "";
                        if (shift != null)
                        {
                            VoxelMove m = VoxelOps.Shift(dest.Data, dest.Width, dest.Height, dest.Depth, sel, shift[0], shift[1], shift[2], false);
                            if (m == null) { throw new CliError("moving the block by (" + shift[0] + "," + shift[1] + "," + shift[2] + ") would push it outside the model (" + box + "); nothing was changed"); }

                            movedText = ", moved by (" + shift[0] + "," + shift[1] + "," + shift[2] + ")";
                        }
                        dest.NotifyChanged();
                        if (src != dest) { src.NotifyChanged(); }

                        msg = n + " voxel(s) " + (toLayer ? "cut into new layer [" + layer + "] \"" + layerName + "\" (frame " + f + " only)" : "selected") + movedText;
                    }
                    Cmds.SaveAndReport(anim, outp, msg);
                };
            }));
        }

        private static string Where2(int frame, int layer) { return "frame " + frame + " layer " + layer; }

        private static ImageFit ParseFit(string s)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "contain": return ImageFit.Contain;
                case "center": case "centre": return ImageFit.Center;
                case "topleft": case "top-left": return ImageFit.TopLeft;
                case "stretch": return ImageFit.Stretch;
            }
            throw new CliError("--fit must be contain, center, topleft or stretch", 2);
        }

        private static LoadedImage LoadImage(string path)
        {
            if (!File.Exists(path)) { throw new CliError("image not found: " + path); }

            try { return ImageImport.Load(path); }
            catch (Exception e) when (e is IOException || e is ArgumentException || e is NotSupportedException || e is InvalidOperationException)
            {
                throw new CliError("cannot read image '" + path + "': " + Loc.T(e.Message));
            }
        }
    }
}
