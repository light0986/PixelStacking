using System;
using System.Collections.Generic;

namespace PixelStacking.Data
{
    /// <summary>預設庫裡一條「通道」的波形：t 是這一格在整段動作裡的進度。</summary>
    public enum Wave
    {
        /// <summary>sin(2π(次數·t + 相位))：來回擺動，循環。</summary>
        Sine,
        /// <summary>max(0, sin(…))：只往一個方向擺（例如膝蓋只會往後彎）。</summary>
        HalfSine,
        /// <summary>sin(π·t)：從 0 升到最大再回到 0（跳起來、鞠躬），不循環。</summary>
        Arc,
        /// <summary>−cos(π·t)：從 −1 平滑揮到 +1（揮砍），不循環。</summary>
        Swing,
        /// <summary>t：從 0 線性增加到 1（拿 360 當幅度＝轉一圈），循環。</summary>
        Spin,
        /// <summary>固定 1（靜態姿勢、舉起的手）。</summary>
        Const,
    }

    /// <summary>通道把算出來的值寫到哪裡。</summary>
    public enum ChannelTarget { Angle, OffsetX, OffsetY, OffsetZ, BodyX, BodyY, BodyZ }

    /// <summary>
    /// 預設庫的一條通道：「哪個角色的部件」＋「寫到角度還是平移」＋波形、幅度、相位。
    /// Role 是角色鍵（左腿、右臂…），使用者在對話框裡把每個角色對到自己的部件（也會依部件名稱自動猜）；身體平移不需要對應（Role = "body"）。
    /// </summary>
    public sealed class PresetChannel
    {
        public string Role;
        public ChannelTarget Target;
        public Wave Wave;
        public double Amp;
        public double Phase;
        public double Freq = 1;
        public bool Invert;

        public PresetChannel(string role, ChannelTarget target, Wave wave, double amp, double phase = 0, double freq = 1, bool invert = false)
        {
            Role = role; Target = target; Wave = wave; Amp = amp; Phase = phase; Freq = freq; Invert = invert;
        }

        public PresetChannel Copy() { return (PresetChannel)MemberwiseClone(); }

        public bool IsBody => Target == ChannelTarget.BodyX || Target == ChannelTarget.BodyY || Target == ChannelTarget.BodyZ;

        /// <summary>這條通道在進度 t 的值（已乘上幅度與正負號）。</summary>
        public double Value(double t)
        {
            double w;
            switch (Wave)
            {
                case Wave.Sine: w = Math.Sin(2 * Math.PI * (Freq * t + Phase)); break;
                case Wave.HalfSine: w = Math.Max(0, Math.Sin(2 * Math.PI * (Freq * t + Phase))); break;
                case Wave.Arc: w = Math.Sin(Math.PI * t); break;
                case Wave.Swing: w = -Math.Cos(Math.PI * t); break;
                case Wave.Spin: w = t; break;
                default: w = 1; break;
            }
            if (Math.Abs(w) < 1e-12) { w = 0; }
            return (Invert ? -Amp : Amp) * w;
        }
    }

    /// <summary>
    /// 緩動：把動作的進度 t（0~1）重新分配——同樣的幀數，動作的快慢節奏不同。作用在整個動作的時間軸上
    /// （所有通道一起），所以循環動作的接縫仍然是接得上的（t = 1 時回到 1）。
    /// </summary>
    public enum Easing
    {
        /// <summary>線性：每一幀等距前進。</summary>
        Linear,
        /// <summary>緩入：一開始慢、越來越快（t²）。</summary>
        EaseIn,
        /// <summary>緩出：一開始快、最後慢慢停下來。</summary>
        EaseOut,
        /// <summary>緩入緩出：兩頭慢、中間快（餘弦）。</summary>
        EaseInOut,
        /// <summary>回彈：衝過頭一點再彈回來（easeOutBack）。</summary>
        Back,
        /// <summary>彈跳：像球落地彈幾下（easeOutBounce）。</summary>
        Bounce,
    }

    /// <summary>一個預設：名稱、說明、要不要循環、預設幀數、預設緩動、一串通道。</summary>
    public sealed class PosePreset
    {
        public string Name;
        public string Description;
        /// <summary>true = 循環動作（第 i 幀的進度 = i / 幀數，最後一幀接回第一幀）；false = 一次性動作（進度從 0 到 1 剛好跑完）。</summary>
        public bool Loop;
        public int DefaultFrames;
        public Easing DefaultEasing = Easing.Linear;
        /// <summary>true = 使用者自己存的預設（存在 presets.txt），false = 內建的。</summary>
        public bool IsCustom;
        public List<PresetChannel> Channels = new List<PresetChannel>();
    }

    /// <summary>角色鍵、顯示名稱、依部件名稱自動猜測的規則，以及內建的預設庫與「產生姿勢」。</summary>
    public static class PosePresets
    {
        public const int MaxFrames = 64;

        /// <summary>緩動的顯示名稱（跟 <see cref="Easing"/> 的順序一致）。</summary>
        /// <summary>波形的顯示名稱（跟 <see cref="Wave"/> 的順序一致）。</summary>
        public static string[] WaveNames => new[] { Loc.Get(TextKey.PosePresets_SwingBackAndForthSine), Loc.Get(TextKey.PosePresets_OneWaySwingOneSide), Loc.Get(TextKey.PosePresets_ArcRisesToTheMax), Loc.Get(TextKey.PosePresets_Wave1To1), Loc.Get(TextKey.PosePresets_FullTurn0To1), Loc.Get(TextKey.PosePresets_Constant) };

        public static string[] EasingNames => new[] { Loc.Get(TextKey.PosePresets_LinearConstantSpeed), Loc.Get(TextKey.PosePresets_EaseInSlowThenFast), Loc.Get(TextKey.PosePresets_EaseOutFastThenSlow), Loc.Get(TextKey.PosePresets_EaseInOutSlowAt), Loc.Get(TextKey.PosePresets_BackOvershootsThenReturns), Loc.Get(TextKey.PosePresets_BounceLikeABallLanding) };

        /// <summary>緩動曲線：t（0~1，超出會夾住）→ 重新分配後的進度。端點一定是 f(0) = 0、f(1) = 1。</summary>
        public static double Ease(Easing e, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            switch (e)
            {
                case Easing.EaseIn: return t * t;
                case Easing.EaseOut: return 1 - (1 - t) * (1 - t);
                case Easing.EaseInOut: return 0.5 - 0.5 * Math.Cos(Math.PI * t);
                case Easing.Back:
                {
                    const double c1 = 1.70158, c3 = c1 + 1;
                    double u = t - 1;
                    return 1 + c3 * u * u * u + c1 * u * u;
                }
                case Easing.Bounce:
                {
                    const double n1 = 7.5625, d1 = 2.75;
                    if (t < 1 / d1) { return n1 * t * t; }
                    if (t < 2 / d1) { t -= 1.5 / d1; return n1 * t * t + 0.75; }
                    if (t < 2.5 / d1) { t -= 2.25 / d1; return n1 * t * t + 0.9375; }
                    t -= 2.625 / d1;
                    return n1 * t * t + 0.984375;
                }
                default: return t;
            }
        }

        /// <summary>所有角色鍵（顯示順序）與中文名稱。</summary>
        public static string[][] Roles => new[]
        {
            new[] { "legL", Loc.Get(TextKey.PosePresets_LeftThigh) }, new[] { "legR", Loc.Get(TextKey.PosePresets_RightThigh) },
            new[] { "kneeL", Loc.Get(TextKey.PosePresets_LeftShin) }, new[] { "kneeR", Loc.Get(TextKey.PosePresets_RightShin) },
            new[] { "armL", Loc.Get(TextKey.PosePresets_LeftUpperArm) }, new[] { "armR", Loc.Get(TextKey.PosePresets_RightUpperArm) },
            new[] { "foreL", Loc.Get(TextKey.PosePresets_LeftForearm) }, new[] { "foreR", Loc.Get(TextKey.PosePresets_RightForearm) },
            new[] { "head", Loc.Get(TextKey.PosePresets_Head) }, new[] { "torso", Loc.Get(TextKey.PosePresets_TorsoWaist) },
            new[] { "wingL", Loc.Get(TextKey.PosePresets_LeftWing) }, new[] { "wingR", Loc.Get(TextKey.PosePresets_RightWing) },
            new[] { "tail1", Loc.Get(TextKey.PosePresets_TailSegment1) }, new[] { "tail2", Loc.Get(TextKey.PosePresets_TailSegment2) }, new[] { "tail3", Loc.Get(TextKey.PosePresets_TailSegment3) },
            // 次級擺動的部件（Q 版角色的頭髮、裙子、披風…）：沒有這些部件的模型完全不受影響，見 IsSecondary
            new[] { "ahoge", Loc.Get(TextKey.PosePresets_Ahoge) }, new[] { "accessory", Loc.Get(TextKey.PosePresets_HairAccessory) },
            new[] { "hairF", Loc.Get(TextKey.PosePresets_FrontHair) }, new[] { "hairB", Loc.Get(TextKey.PosePresets_BackHair) },
            new[] { "skirt", Loc.Get(TextKey.PosePresets_Skirt) }, new[] { "cape", Loc.Get(TextKey.PosePresets_Cape) },
            new[] { "earL", Loc.Get(TextKey.PosePresets_LeftEar) }, new[] { "earR", Loc.Get(TextKey.PosePresets_RightEar) },
            new[] { "spin", Loc.Get(TextKey.PosePresets_RotorPropeller) },
            new[] { "body", Loc.Get(TextKey.PosePresets_WholeBody) },
        };

        public static string RoleLabel(string role)
        {
            foreach (string[] r in Roles) { if (r[0] == role) { return r[1]; } }
            return role;
        }

        // --- 依名稱猜哪個部件是哪個角色 ----------------------------------------------------------------------
        private static bool Has(string name, params string[] keys)
        {
            foreach (string k in keys) { if (name.IndexOf(k, StringComparison.Ordinal) >= 0) { return true; } }
            return false;
        }

        private static int Side(string n)
        {
            // 1 = 左、2 = 右、0 = 看不出來。英文的 l / r 要單獨成一個字（l_leg、leg_l、leg.L），不然 "tail" 裡的 l 會被當成左
            bool left = n.Contains("左") || n.Contains("left") || System.Text.RegularExpressions.Regex.IsMatch(n, @"(^|[^a-z])l([^a-z]|$)");
            bool right = n.Contains("右") || n.Contains("right") || System.Text.RegularExpressions.Regex.IsMatch(n, @"(^|[^a-z])r([^a-z]|$)");
            return left == right ? 0 : left ? 1 : 2;
        }

        /// <summary>這個名稱像不像某個角色（只看名稱，不看左右；左右由呼叫端另外比）。</summary>
        private static bool RoleWord(string role, string n)
        {
            switch (role)
            {
                case "legL": case "legR": return Has(n, "大腿", "腿", "leg", "thigh") && !Has(n, "小腿", "shin", "calf", "knee", "膝");
                case "kneeL": case "kneeR": return Has(n, "小腿", "shin", "calf", "knee", "膝");
                case "armL": case "armR": return Has(n, "上臂", "手臂", "臂", "arm", "shoulder", "肩") && !Has(n, "前臂", "fore", "elbow", "肘");
                case "foreL": case "foreR": return Has(n, "前臂", "fore", "elbow", "肘");
                case "head": return Has(n, "頭", "head") && !Has(n, "髮", "hair", "飾", "帽", "hat", "ribbon", "clip", "呆毛", "ahoge");
                case "torso": return Has(n, "軀", "torso", "chest", "腰", "spine", "胸");
                case "wingL": case "wingR": return Has(n, "翅", "翼", "wing");
                case "tail1": return Has(n, "尾", "tail") && Has(n, "1", "一");
                case "tail2": return Has(n, "尾", "tail") && Has(n, "2", "二");
                case "tail3": return Has(n, "尾", "tail") && Has(n, "3", "三");
                case "spin": return Has(n, "旋", "propeller", "rotor", "blade", "wheel", "輪", "風車");
                case "ahoge": return Has(n, "呆毛", "ahoge", "天線", "antenna");
                case "accessory": return Has(n, "飾", "蝴蝶結", "ribbon", "clip", "髮夾", "hat", "帽") && !Has(n, "呆毛", "ahoge");
                case "hairF": return HairWord(n) && Has(n, "前", "瀏海", "bang", "front");
                case "hairB": return (HairWord(n) && !Has(n, "前", "瀏海", "bang", "front")) || Has(n, "馬尾", "ponytail", "braid", "辮");
                case "skirt": return Has(n, "裙", "skirt");
                case "cape": return Has(n, "披風", "斗篷", "cape", "cloak", "scarf", "圍巾");
                case "earL": case "earR": return Has(n, "耳") || System.Text.RegularExpressions.Regex.IsMatch(n, @"(^|[^a-z])ears?([^a-z]|$)");
            }
            return false;
        }

        /// <summary>名稱像頭髮（髮飾、呆毛不算）。瀏海也算。</summary>
        private static bool HairWord(string n)
        {
            return Has(n, "髮", "hair", "瀏海", "bang") && !Has(n, "飾", "clip", "ribbon", "蝴蝶結", "呆毛", "ahoge");
        }

        /// <summary>
        /// 次級擺動的角色（頭髮、呆毛、髮飾、裙子、披風、耳朵）：內建預設在它們身上放了「跟著身體延遲擺動」的通道，
        /// 但模型沒有這些部件時，預設庫對話框不會把這些通道列出來（有的話才列），所以不會多出一堆沒用到的列。
        /// </summary>
        public static bool IsSecondary(string role)
        {
            switch (role)
            {
                case "ahoge": case "accessory": case "hairF": case "hairB": case "skirt": case "cape": case "earL": case "earR": return true;
                default: return false;
            }
        }

        private static bool NeedsSide(string role) { return role.EndsWith("L") || role.EndsWith("R"); }

        /// <summary>
        /// 依部件名稱猜每個角色對到哪個部件編號（猜不到就不放進結果）。同一個部件不會被兩個角色用到。
        /// 左右角色要名稱裡看得出左／右（左、右、left、right、獨立的 l / r）。
        /// </summary>
        public static Dictionary<string, int> GuessAssignments(Rig rig)
        {
            Dictionary<string, int> result = new Dictionary<string, int>();
            HashSet<int> used = new HashSet<int>();
            foreach (string[] r in Roles)
            {
                string role = r[0];
                if (role == "body") { continue; }

                foreach (Bone b in rig.Bones)
                {
                    if (used.Contains(b.Id)) { continue; }

                    string n = (b.Name ?? "").ToLowerInvariant();
                    if (!RoleWord(role, n)) { continue; }

                    if (NeedsSide(role))
                    {
                        int side = Side(n);
                        if (side != (role.EndsWith("L") ? 1 : 2)) { continue; }
                    }
                    result[role] = b.Id;
                    used.Add(b.Id);
                    break;
                }
            }
            return result;
        }

        // --- 內建預設庫 --------------------------------------------------------------------------------------
        private static PosePreset P(string name, string desc, bool loop, int frames, params PresetChannel[] ch)
        {
            PosePreset p = new PosePreset { Name = name, Description = desc, Loop = loop, DefaultFrames = frames };
            p.Channels.AddRange(ch);
            return p;
        }

        private static PosePreset E(Easing e, PosePreset p) { p.DefaultEasing = e; return p; }

        /// <summary>
        /// 內建預設的方向以「角色面朝 +Y、所有部件的轉軸是 X」為準（右手座標，繞 X 轉）：往下垂的部件（大腿、上臂、前臂）正角度＝往前，
        /// 但往上伸的部件（軀幹、頭）正角度＝往後仰，小腿要往後彎才是正常的膝蓋——所以這四個角色（小腿、軀幹、頭）的通道預設就是「反向」，
        /// 這樣面朝 +Y 的角色開箱就是自然的動作；面朝 −Y 的角色，在預設庫把左右大腿、上臂、前臂與這四個角色全部切換一次「反向」即可。
        /// </summary>
        private static bool NaturalInvert(string role)
        {
            // 往上伸的部件（小腿往後彎、軀幹、頭、呆毛、耳朵）正角度＝往後，所以預設都反向
            return role == "kneeL" || role == "kneeR" || role == "torso" || role == "head" || role == "ahoge" || role == "earL" || role == "earR";
        }

        private static PresetChannel C(string role, ChannelTarget t, Wave w, double amp, double phase = 0, double freq = 1, bool invert = false)
        {
            return new PresetChannel(role, t, w, amp, phase, freq, invert || NaturalInvert(role));
        }

        /// <summary>每次呼叫都回傳新的一份（對話框會修改幅度、正負號，不能動到共用的內建值）。</summary>
        public static List<PosePreset> BuiltIn()
        {
            const ChannelTarget A = ChannelTarget.Angle, BZ = ChannelTarget.BodyZ;
            return new List<PosePreset>
            {
                P("走路", Loc.Get(TextKey.PosePresets_LegsAndArmsSwingBack), true, 8,
                    C("legL", A, Wave.Sine, 30, 0), C("legR", A, Wave.Sine, 30, 0.5),
                    C("kneeL", A, Wave.HalfSine, 35, 0.15), C("kneeR", A, Wave.HalfSine, 35, 0.65),
                    C("armL", A, Wave.Sine, 25, 0.5), C("armR", A, Wave.Sine, 25, 0),
                    C("body", BZ, Wave.Sine, 1, 0.25, 2),
                    // 次級擺動（頭髮、呆毛、裙子、披風、耳朵）：頻率跟身體上下起伏一樣（每步一次），相位晚一點＝延遲跟上
                    C("hairB", A, Wave.Sine, 8, 0.4, 2), C("hairF", A, Wave.Sine, 4, 0.45, 2), C("ahoge", A, Wave.Sine, 14, 0.55, 2), C("accessory", A, Wave.Sine, 5, 0.5, 2),
                    C("skirt", A, Wave.Sine, 5, 0.3), C("cape", A, Wave.Const, -8), C("cape", A, Wave.Sine, 6, 0.4, 2),
                    C("earL", A, Wave.Sine, 8, 0.5, 2), C("earR", A, Wave.Sine, 8, 0.5, 2)),
                P("跑步", Loc.Get(TextKey.PosePresets_TheSameSetAsWalk), true, 8,
                    C("legL", A, Wave.Sine, 50, 0), C("legR", A, Wave.Sine, 50, 0.5),
                    C("kneeL", A, Wave.HalfSine, 70, 0.15), C("kneeR", A, Wave.HalfSine, 70, 0.65),
                    C("armL", A, Wave.Sine, 45, 0.5), C("armR", A, Wave.Sine, 45, 0),
                    C("body", BZ, Wave.Sine, 2, 0.25, 2),
                    C("hairB", A, Wave.Sine, 14, 0.4, 2), C("hairF", A, Wave.Sine, 8, 0.45, 2), C("ahoge", A, Wave.Sine, 22, 0.55, 2), C("accessory", A, Wave.Sine, 8, 0.5, 2),
                    C("skirt", A, Wave.Sine, 9, 0.3), C("cape", A, Wave.Const, -25), C("cape", A, Wave.Sine, 10, 0.4, 2),
                    C("earL", A, Wave.Sine, 14, 0.5, 2), C("earR", A, Wave.Sine, 14, 0.5, 2)),
                P("待機（呼吸）", Loc.Get(TextKey.PosePresets_TheBodySlowlyRisesAnd), true, 8,
                    C("body", BZ, Wave.Sine, 1, 0),
                    C("armL", A, Wave.Sine, 6, 0), C("armR", A, Wave.Sine, 6, 0.5),
                    C("head", A, Wave.Sine, 4, 0.25),
                    C("hairB", A, Wave.Sine, 3, 0.4), C("hairF", A, Wave.Sine, 2, 0.45), C("ahoge", A, Wave.Sine, 6, 0.5), C("accessory", A, Wave.Sine, 2, 0.5),
                    C("skirt", A, Wave.Sine, 2, 0.3), C("cape", A, Wave.Sine, 3, 0.35), C("earL", A, Wave.Sine, 4, 0.5), C("earR", A, Wave.Sine, 4, 0.5)),
                P("揮手", Loc.Get(TextKey.PosePresets_TheRightHandIsRaised), true, 8,
                    C("armR", A, Wave.Const, 120), C("foreR", A, Wave.Sine, 30, 0)),
                E(Easing.EaseIn, P("攻擊（揮砍）", Loc.Get(TextKey.PosePresets_TheRightArmSwingsDown), false, 6,
                    C("armR", A, Wave.Swing, 70), C("foreR", A, Wave.Swing, 20), C("torso", A, Wave.Swing, 15),
                    C("hairB", A, Wave.Swing, 10), C("cape", A, Wave.Swing, 15), C("skirt", A, Wave.Swing, 6))),
                P("跳躍", Loc.Get(TextKey.PosePresets_TheWholeBodyJumpsUp), false, 8,
                    C("body", BZ, Wave.Arc, 5),
                    C("legL", A, Wave.Arc, 35), C("legR", A, Wave.Arc, 35),
                    C("kneeL", A, Wave.Arc, 60), C("kneeR", A, Wave.Arc, 60),
                    C("armL", A, Wave.Arc, 60), C("armR", A, Wave.Arc, 60),
                    C("hairB", A, Wave.Arc, 12), C("hairF", A, Wave.Arc, 8), C("ahoge", A, Wave.Arc, 16), C("accessory", A, Wave.Arc, 6),
                    C("skirt", A, Wave.Arc, 8), C("cape", A, Wave.Arc, 20), C("earL", A, Wave.Arc, 12), C("earR", A, Wave.Arc, 12)),
                P("坐下", Loc.Get(TextKey.PosePresets_StaticPoseMakesOnly1), false, 1,
                    C("legL", A, Wave.Const, 90), C("legR", A, Wave.Const, 90),
                    C("kneeL", A, Wave.Const, 90), C("kneeR", A, Wave.Const, 90),
                    C("body", BZ, Wave.Const, -2)),
                E(Easing.EaseInOut, P("鞠躬", Loc.Get(TextKey.PosePresets_TheTorsoBendsForwardAnd), false, 6,
                    C("torso", A, Wave.Arc, 40), C("head", A, Wave.Arc, 15),
                    C("hairF", A, Wave.Arc, 12), C("hairB", A, Wave.Arc, 20), C("cape", A, Wave.Arc, 12), C("ahoge", A, Wave.Arc, 10))),
                P("點頭／搖頭", Loc.Get(TextKey.PosePresets_TheHeadTurnsBackAnd), true, 8,
                    C("head", A, Wave.Sine, 20, 0),
                    C("hairB", A, Wave.Sine, 6, 0.3), C("hairF", A, Wave.Sine, 3, 0.3), C("ahoge", A, Wave.Sine, 12, 0.45), C("accessory", A, Wave.Sine, 4, 0.4),
                    C("earL", A, Wave.Sine, 6, 0.4), C("earR", A, Wave.Sine, 6, 0.4)),
                P("拍翅膀", Loc.Get(TextKey.PosePresets_BothWingsFlapUpAnd), true, 8,
                    C("wingL", A, Wave.Sine, 40, 0), C("wingR", A, Wave.Sine, 40, 0, 1, true),
                    C("body", BZ, Wave.Sine, 1, 0.25)),
                P("搖尾巴", Loc.Get(TextKey.PosePresets_ThreeTailSegmentsSwingOne), true, 8,
                    C("tail1", A, Wave.Sine, 15, 0), C("tail2", A, Wave.Sine, 20, 0.12), C("tail3", A, Wave.Sine, 25, 0.24)),
                P("旋轉（螺旋槳）", Loc.Get(TextKey.PosePresets_TheRotatingPartMakesA), true, 8,
                    C("spin", A, Wave.Spin, 360)),
            };
        }

        // --- 產生姿勢 ----------------------------------------------------------------------------------------
        /// <summary>第 i 幀（共 frames 幀）的進度 t：循環 = i / frames；一次性 = i / (frames − 1)，只有 1 幀時 t = 1（動作做完的樣子）。</summary>
        public static double ProgressOf(bool loop, int i, int frames)
        {
            if (loop) { return (double)i / frames; }
            return frames <= 1 ? 1.0 : (double)i / (frames - 1);
        }

        /// <summary>
        /// 依預設產生每一幀的姿勢。channels 的 Role 要在 assign 裡有對到部件才會作用（身體平移通道不用對應）；
        /// 對不到的通道略過。其餘部件維持休息姿勢（全 0）。easing 先把每一幀的進度重新分配（見 <see cref="Easing"/>）。
        /// </summary>
        public static List<Pose> Generate(IList<PresetChannel> channels, bool loop, int frames, IDictionary<string, int> assign, Easing easing = Easing.Linear)
        {
            if (frames < 1 || frames > MaxFrames) { throw new ArgumentException((Loc.Format(TextKey.PosePresets_FrameCountRange, MaxFrames))); }

            List<Pose> poses = new List<Pose>();
            for (int i = 0; i < frames; i++)
            {
                double t = Ease(easing, ProgressOf(loop, i, frames));
                Pose pose = new Pose();
                foreach (PresetChannel c in channels)
                {
                    double v = c.Value(t);
                    if (c.IsBody)
                    {
                        if (c.Target == ChannelTarget.BodyX) { pose.BodyX += v; }
                        else if (c.Target == ChannelTarget.BodyY) { pose.BodyY += v; }
                        else { pose.BodyZ += v; }
                        continue;
                    }

                    int id;
                    if (assign == null || !assign.TryGetValue(c.Role, out id) || id == 0) { continue; }

                    BonePose bp = pose.Of(id);
                    switch (c.Target)
                    {
                        case ChannelTarget.Angle: bp.Angle += v; break;
                        case ChannelTarget.OffsetX: bp.OffsetX += v; break;
                        case ChannelTarget.OffsetY: bp.OffsetY += v; break;
                        default: bp.OffsetZ += v; break;
                    }
                }
                poses.Add(pose);
            }
            return poses;
        }
    }
}
