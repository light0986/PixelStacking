using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PixelStacking.Data;

namespace PixelStacking.Cli
{
    /// <summary>骨架（部件與關節）、姿勢烘焙、姿勢預設庫。</summary>
    internal static class CmdsRig
    {
        private static string Out(CliArgs a, string file) { return a.Str("out", file); }

        private static Bone FindBone(Animation anim, string spec)
        {
            Rig rig = anim.Rig;
            if (spec.StartsWith("#", StringComparison.Ordinal))
            {
                int id;
                if (int.TryParse(spec.Substring(1), out id) && rig.Find(id) != null) { return rig.Find(id); }
            }
            foreach (Bone b in rig.Bones) { if (string.Equals(b.Name, spec, StringComparison.OrdinalIgnoreCase)) { return b; } }

            string known = rig.Count == 0 ? "(the rig has no bones yet: use rig-add)" : string.Join(", ", rig.Bones.Select(b => "\"" + b.Name + "\" (#" + b.Id + ")"));
            throw new CliError("no bone named '" + spec + "'. Bones: " + known);
        }

        private static List<VoxelModel> Frames(Animation anim, string spec)
        {
            List<VoxelModel> list = new List<VoxelModel>();
            if (spec.ToLowerInvariant() == "all") { list.AddRange(anim.Frames); }
            else { list.Add(anim.Frames[Cmds.FrameIndex(anim, spec)]); }
            return list;
        }

        private static string EnglishName(string name) { string t = Loc.T(name); return t; }

        private static PosePreset FindPreset(string spec)
        {
            List<PosePreset> all = PosePresets.BuiltIn();
            int n;
            if (int.TryParse(spec, out n))
            {
                if (n < 0 || n >= all.Count) { throw new CliError("preset number must be 0.." + (all.Count - 1) + " (see `pstk presets`)"); }

                return all[n];
            }
            foreach (PosePreset p in all)
            {
                if (string.Equals(p.Name, spec, StringComparison.OrdinalIgnoreCase) || string.Equals(EnglishName(p.Name), spec, StringComparison.OrdinalIgnoreCase)) { return p; }
            }
            throw new CliError("no preset '" + spec + "'. Run `pstk presets` for the list.");
        }

        private static Easing ParseEasing(string s)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "linear": return Easing.Linear;
                case "in": case "easein": return Easing.EaseIn;
                case "out": case "easeout": return Easing.EaseOut;
                case "inout": case "easeinout": return Easing.EaseInOut;
                case "back": return Easing.Back;
                case "bounce": return Easing.Bounce;
            }
            throw new CliError("--easing must be linear, in, out, inout, back or bounce", 2);
        }

        private static bool SameVoxels(VoxelModel a, VoxelModel b)
        {
            for (int i = 0; i < a.Data.Length; i++) { if (a.Data[i] != b.Data[i]) { return false; } }
            return true;
        }

        internal static void Register(List<Cmd> c)
        {
            c.Add(new Cmd("Rig (parts & joints)", "rig-add", "rig-add <file> <name> <pivotX> <pivotY> <pivotZ> [--axis X|Y|Z] [--parent bone]",
                "Add a part/bone. pivot = joint position in voxel coordinates (may be fractional). axis = rotation axis (default X: swings forward/back for a character facing +Y). --parent = bone this one hangs from (e.g. shin -> thigh). Name parts like 左大腿/右大腿/頭 or left_thigh/right_thigh/head so presets can find them.", a =>
            {
                string file = a.PosStr(0, "file"), name = a.PosStr(1, "name");
                double px = a.PosDbl(2, "pivotX"), py = a.PosDbl(3, "pivotY"), pz = a.PosDbl(4, "pivotZ");
                a.NeedExactly(5, "rig-add <file> <name> <pivotX> <pivotY> <pivotZ>");
                string axis = (a.Str("axis", "X") ?? "X").ToUpperInvariant(), parent = a.Str("parent"), outp = Out(a, file);
                if (axis != "X" && axis != "Y" && axis != "Z") { throw new CliError("--axis must be X, Y or Z", 2); }

                return () =>
                {
                    Animation anim = Cmds.Open(file);
                    foreach (Bone e in anim.Rig.Bones) { if (e.Name == name) { throw new CliError("a bone named '" + name + "' already exists (id " + e.Id + ")"); } }

                    Bone pb = parent == null ? null : FindBone(anim, parent);
                    Bone b = anim.Rig.Add(name, px, py, pz);
                    if (b == null) { throw new CliError("the rig is full (" + Rig.MaxBones + " bones)"); }

                    b.Axis = axis == "X" ? BoneAxis.X : axis == "Y" ? BoneAxis.Y : BoneAxis.Z;
                    if (pb != null && !anim.Rig.SetParent(b.Id, pb.Id)) { throw new CliError("cannot use '" + parent + "' as the parent"); }

                    anim.Rig.NotifyChanged();
                    Cmds.SaveAndReport(anim, outp, "added bone #" + b.Id + " \"" + name + "\" axis " + b.Axis + " pivot (" + Cmds.Num(px) + "," + Cmds.Num(py) + "," + Cmds.Num(pz) + ")" + (pb != null ? " parent \"" + pb.Name + "\"" : " parent (body)"));
                };
            }));

            c.Add(new Cmd("Rig (parts & joints)", "rig-remove", "rig-remove <file> <bone>", "Delete a bone (its children move up to its parent). Voxel marks of that bone become body.", a =>
            {
                string file = a.PosStr(0, "file"), bone = a.PosStr(1, "bone");
                a.NeedExactly(2, "rig-remove <file> <bone>");
                string outp = Out(a, file);
                return () =>
                {
                    Animation anim = Cmds.Open(file);
                    Bone b = FindBone(anim, bone);
                    foreach (VoxelModel f in anim.Frames)
                    {
                        if (f.Parts == null) { continue; }

                        for (int i = 0; i < f.Parts.Length; i++) { if (f.Parts[i] == b.Id) { f.Parts[i] = 0; } }
                    }
                    anim.Rig.Remove(b.Id);
                    Cmds.SaveAndReport(anim, outp, "removed bone \"" + bone + "\"");
                };
            }));

            c.Add(new Cmd("Rig (parts & joints)", "rig-mark", "rig-mark <file> <bone> box <x0> <y0> <z0> <x1> <y1> <z1> | wand <x> <y> <z> | clear   [--same-colour] [--diagonal] [--frame N|all]",
                "Mark voxels as belonging to a bone. 'box' marks every coloured voxel in the box; 'wand' marks the whole 3D-connected block containing a voxel (cut the model into parts by connectivity); 'clear' removes this bone's marks. Marks are per frame: use --frame all when the frames are copies. Default frame 0. Unmarked voxels are the body.", a =>
            {
                string file = a.PosStr(0, "file"), bone = a.PosStr(1, "bone"), mode = a.PosStr(2, "box|wand|clear").ToLowerInvariant();
                int[] n = null;
                if (mode == "box") { n = new int[6]; for (int i = 0; i < 6; i++) { n[i] = a.PosInt(3 + i, "box coordinate"); } a.NeedExactly(9, "rig-mark <file> <bone> box x0 y0 z0 x1 y1 z1"); }
                else if (mode == "wand") { n = new int[3]; for (int i = 0; i < 3; i++) { n[i] = a.PosInt(3 + i, "wand coordinate"); } a.NeedExactly(6, "rig-mark <file> <bone> wand x y z"); }
                else if (mode == "clear") { a.NeedExactly(3, "rig-mark <file> <bone> clear"); }
                else { throw new CliError("third argument must be box, wand or clear", 2); }

                bool same = a.Has("same-colour"), diag = a.Has("diagonal");
                string fr = a.Str("frame", "0"), outp = Out(a, file);
                return () =>
                {
                    Animation anim = Cmds.Open(file);
                    Bone b = FindBone(anim, bone);
                    List<VoxelModel> frames = Frames(anim, fr);
                    int total = 0;
                    foreach (VoxelModel m in frames)
                    {
                        int w = m.Width, h = m.Height, d = m.Depth, wh = w * h;
                        bool[] mask = null;
                        if (mode == "wand")
                        {
                            if (!m.InBounds(n[0], n[1], n[2])) { throw new CliError("(" + n[0] + "," + n[1] + "," + n[2] + ") is outside the model"); }

                            mask = VoxelOps.Component(m.Data, w, h, d, n[2] * wh + n[1] * w + n[0], same, diag ? WandConnectivity.Corners : WandConnectivity.Faces);
                            if (mask == null) { throw new CliError("there is no voxel at (" + n[0] + "," + n[1] + "," + n[2] + ") in frame " + anim.IndexOf(m)); }
                        }
                        if (mode == "clear" && m.Parts == null) { continue; }

                        byte[] parts = m.EnsureParts();
                        for (int i = 0; i < m.Data.Length; i++)
                        {
                            if (mode == "clear") { if (parts[i] == b.Id) { parts[i] = 0; total++; } continue; }
                            if (m.Data[i] == 0) { continue; }

                            bool inside;
                            if (mode == "wand") { inside = mask[i]; }
                            else
                            {
                                int z = i / wh, r = i - z * wh, y = r / w, x = r - y * w;
                                inside = x >= Math.Min(n[0], n[3]) && x <= Math.Max(n[0], n[3]) && y >= Math.Min(n[1], n[4]) && y <= Math.Max(n[1], n[4]) && z >= Math.Min(n[2], n[5]) && z <= Math.Max(n[2], n[5]);
                            }
                            if (inside && parts[i] != b.Id) { parts[i] = (byte)b.Id; total++; }
                        }
                    }
                    Cmds.SaveAndReport(anim, outp, (mode == "clear" ? "cleared " : "marked ") + total + " voxel(s) for bone \"" + b.Name + "\" in " + frames.Count + " frame(s)");
                };
            }));

            c.Add(new Cmd("Rig (parts & joints)", "bake", "bake <file> [--frame N] [--angle bone=deg]... [--offset bone=dx,dy,dz]... [--body dx,dy,dz] [--at I]",
                "Strike one pose and save it as a NEW frame (the source frame is untouched). --angle can repeat. Rotation is around the bone's axis through its pivot; children follow their parent. --body moves the whole body.", a =>
            {
                string file = a.PosStr(0, "file");
                a.NeedExactly(1, "bake <file>");
                string fr = a.Str("frame", "0"), body = a.Str("body"), at = a.Str("at"), outp = Out(a, file);
                List<string> angles = a.All("angle"), offsets = a.All("offset");
                return () =>
                {
                    Animation anim = Cmds.Open(file);
                    VoxelModel src = anim.Frames[Cmds.FrameIndex(anim, fr)];
                    Pose pose = new Pose();
                    foreach (string s in angles)
                    {
                        int eq = s.LastIndexOf('=');
                        if (eq <= 0) { throw new CliError("--angle needs bone=degrees (got '" + s + "')", 2); }

                        pose.Of(FindBone(anim, s.Substring(0, eq)).Id).Angle = CliArgs.ParseDouble(s.Substring(eq + 1), "--angle");
                    }
                    foreach (string s in offsets)
                    {
                        int eq = s.LastIndexOf('=');
                        if (eq <= 0) { throw new CliError("--offset needs bone=dx,dy,dz (got '" + s + "')", 2); }

                        string[] v = s.Substring(eq + 1).Split(',');
                        if (v.Length != 3) { throw new CliError("--offset needs bone=dx,dy,dz (got '" + s + "')", 2); }

                        BonePose bp = pose.Of(FindBone(anim, s.Substring(0, eq)).Id);
                        bp.OffsetX = CliArgs.ParseDouble(v[0], "--offset"); bp.OffsetY = CliArgs.ParseDouble(v[1], "--offset"); bp.OffsetZ = CliArgs.ParseDouble(v[2], "--offset");
                    }
                    if (body != null)
                    {
                        string[] v = body.Split(',');
                        if (v.Length != 3) { throw new CliError("--body needs dx,dy,dz", 2); }

                        pose.BodyX = CliArgs.ParseDouble(v[0], "--body"); pose.BodyY = CliArgs.ParseDouble(v[1], "--body"); pose.BodyZ = CliArgs.ParseDouble(v[2], "--body");
                    }
                    int pos = at == null ? anim.Count : CliArgs.ParseInt(at, "--at");
                    if (pos < 0 || pos > anim.Count) { throw new CliError("--at must be 0.." + anim.Count); }

                    VoxelModel baked = RigBake.Bake(src, anim.Rig, pose);
                    if (SameVoxels(baked, src)) { Console.Error.WriteLine("warning: the baked frame is identical to the source (are the parts marked? is any angle non-zero?)"); }

                    anim.Insert(pos, baked);
                    Cmds.SaveAndReport(anim, outp, "baked a pose into new frame " + pos + "; now " + anim.Count + " frame(s)");
                };
            }));

            c.Add(new Cmd("Rig (parts & joints)", "presets", "presets", "List the built-in pose presets (walk, run, idle, attack, jump...), their roles and defaults.", a =>
            {
                a.NeedExactly(0, "presets");
                return () =>
                {
                    List<PosePreset> all = PosePresets.BuiltIn();
                    for (int i = 0; i < all.Count; i++)
                    {
                        PosePreset p = all[i];
                        string[] roles = p.Channels.Where(ch => !ch.IsBody).Select(ch => ch.Role).Distinct().ToArray();
                        bool body = p.Channels.Any(ch => ch.IsBody);
                        Console.WriteLine(i.ToString().PadLeft(2) + "  " + EnglishName(p.Name) + "  [" + p.Name + "]  " + (p.Loop ? "loops" : "one-shot") + ", " + p.DefaultFrames + " frames, easing " + p.DefaultEasing);
                        Console.WriteLine("      " + Loc.T(p.Description));
                        Console.WriteLine("      roles: " + string.Join(", ", roles) + (body ? (roles.Length > 0 ? ", " : "") + "(body movement)" : ""));
                    }
                    Console.WriteLine();
                    Console.WriteLine("role ids and meaning:");
                    foreach (string[] r in PosePresets.Roles) { Console.WriteLine("  " + r[0].PadRight(7) + Loc.T(r[1])); }
                };
            }));

            c.Add(new Cmd("Rig (parts & joints)", "preset", "preset <file> <preset> [--frames N] [--easing linear|in|out|inout|back|bounce] [--source-frame N] [--amp F] [--assign role=bone,...] [--reverse role,...] [--reverse-all] [--loop|--no-loop]",
                "Generate a whole animation from a preset (name, English name or number from `pstk presets`) and append the frames. Roles are matched to bones by name automatically (left/right + thigh/shin/arm/head...); override with --assign. --amp scales every swing; --reverse flips the swing direction of those roles.", a =>
            {
                string file = a.PosStr(0, "file"), presetSpec = a.PosStr(1, "preset");
                a.NeedExactly(2, "preset <file> <preset>");
                string framesS = a.Str("frames"), easingS = a.Str("easing"), srcS = a.Str("source-frame", "0"), assignS = a.Str("assign"), reverseS = a.Str("reverse");
                double amp = a.Dbl("amp", 1);
                bool reverseAll = a.Has("reverse-all"), loopOn = a.Has("loop"), loopOff = a.Has("no-loop");
                string outp = Out(a, file);
                return () =>
                {
                    Animation anim = Cmds.Open(file);
                    PosePreset p = FindPreset(presetSpec);
                    int frames = framesS == null ? p.DefaultFrames : CliArgs.ParseInt(framesS, "--frames");
                    Easing easing = easingS == null ? p.DefaultEasing : ParseEasing(easingS);
                    bool loop = loopOn ? true : loopOff ? false : p.Loop;
                    VoxelModel src = anim.Frames[Cmds.FrameIndex(anim, srcS, "--source-frame")];
                    if (anim.Rig.Count == 0 && !p.Channels.All(ch => ch.IsBody)) { throw new CliError("the rig has no bones. Add parts with rig-add and mark voxels with rig-mark first."); }

                    Dictionary<string, int> assign = PosePresets.GuessAssignments(anim.Rig);
                    if (assignS != null)
                    {
                        foreach (string pair in assignS.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            int eq = pair.IndexOf('=');
                            if (eq <= 0) { throw new CliError("--assign needs role=bone pairs (got '" + pair + "')", 2); }

                            string role = pair.Substring(0, eq).Trim();
                            if (!PosePresets.Roles.Any(r => r[0] == role)) { throw new CliError("unknown role '" + role + "' (see `pstk presets`)", 2); }

                            assign[role] = FindBone(anim, pair.Substring(eq + 1).Trim()).Id;
                        }
                    }

                    HashSet<string> reverse = new HashSet<string>((reverseS ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
                    List<PresetChannel> channels = new List<PresetChannel>();
                    foreach (PresetChannel ch in p.Channels)
                    {
                        PresetChannel cc = ch.Copy();
                        cc.Amp *= amp;
                        if (!cc.IsBody && (reverse.Contains(cc.Role) || reverseAll)) { cc.Invert = !cc.Invert; }

                        channels.Add(cc);
                    }

                    HashSet<int> seen = new HashSet<int>();
                    foreach (KeyValuePair<string, int> kv in assign)
                    {
                        if (!channels.Any(ch => ch.Role == kv.Key)) { continue; }

                        if (!seen.Add(kv.Value)) { throw new CliError("one bone cannot serve two roles (check --assign)"); }
                    }
                    bool anyUsed = channels.Any(ch => ch.IsBody) || channels.Any(ch => assign.ContainsKey(ch.Role));
                    if (!anyUsed) { throw new CliError("none of this preset's roles matched a bone. Name bones like left_thigh / head or pass --assign role=bone. Roles: " + string.Join(", ", channels.Select(ch => ch.Role).Distinct())); }

                    List<Pose> poses = PosePresets.Generate(channels, loop, frames, assign, easing);
                    List<VoxelModel> baked = new List<VoxelModel>();
                    bool anyChange = false;
                    foreach (Pose pose in poses)
                    {
                        VoxelModel f = RigBake.Bake(src, anim.Rig, pose);
                        baked.Add(f);
                        if (!anyChange && !SameVoxels(f, src)) { anyChange = true; }
                    }
                    if (!anyChange) { throw new CliError("every generated frame equals the source frame: the bones used by this preset have no marked voxels in frame " + srcS + " (use rig-mark), or all angles are zero"); }

                    int first = anim.Count;
                    foreach (VoxelModel f in baked) { anim.Insert(anim.Count, f); }
                    string used = string.Join(", ", assign.Where(kv => channels.Any(ch => ch.Role == kv.Key)).Select(kv => kv.Key + "=\"" + anim.Rig.Find(kv.Value).Name + "\""));
                    Cmds.SaveAndReport(anim, outp, "generated '" + EnglishName(p.Name) + "': " + baked.Count + " frame(s) appended as frames " + first + ".." + (anim.Count - 1) + " (loop " + (loop ? "yes" : "no") + ", easing " + easing + ")\nroles used: " + (used.Length == 0 ? "(body only)" : used));
                };
            }));
        }
    }
}
