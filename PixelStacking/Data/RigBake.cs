using System;
using System.Collections.Generic;

namespace PixelStacking.Data
{
    /// <summary>一個部件在某個姿勢下的轉動角度（度）與平移（體素）。</summary>
    public sealed class BonePose
    {
        public double Angle, OffsetX, OffsetY, OffsetZ;
    }

    /// <summary>
    /// 一個完整的姿勢：身體的平移＋每個部件的角度與平移（部件編號 → <see cref="BonePose"/>）。沒列出的部件就是休息姿勢（全 0）。
    /// 骨架上記的「目前姿勢」用 <see cref="FromRig"/> 取得；預設庫產生的每一格姿勢也是這個型別。
    /// </summary>
    public sealed class Pose
    {
        public double BodyX, BodyY, BodyZ;
        public readonly Dictionary<int, BonePose> Bones = new Dictionary<int, BonePose>();

        public static Pose FromRig(Rig rig)
        {
            Pose p = new Pose { BodyX = rig.BodyX, BodyY = rig.BodyY, BodyZ = rig.BodyZ };
            foreach (Bone b in rig.Bones)
            {
                p.Bones[b.Id] = new BonePose { Angle = b.Angle, OffsetX = b.OffsetX, OffsetY = b.OffsetY, OffsetZ = b.OffsetZ };
            }
            return p;
        }

        /// <summary>取（必要時新增）某個部件的姿勢，預設全 0。</summary>
        public BonePose Of(int boneId)
        {
            BonePose bp;
            if (!Bones.TryGetValue(boneId, out bp)) { bp = new BonePose(); Bones[boneId] = bp; }
            return bp;
        }

        public BonePose Peek(int boneId)
        {
            BonePose bp;
            return Bones.TryGetValue(boneId, out bp) ? bp : null;
        }
    }

    /// <summary>
    /// 把「帶部件標記的一幀 + 姿勢」烘焙成一幀普通的體素：沒有標記（部件 0）的是身體，只跟著身體的平移；每個部件依它的關節轉動、再平移，
    /// 子部件跟著上一層一起動（正向運動學，沒有 IK；直接掛在身體上的部件也跟著身體的平移）。部件各自動完之後疊起來，編號大的蓋住編號小的，身體墊底。
    /// 做法是反向取樣：結果的每一格，倒推回「動之前在哪一格」，最近鄰，所以不會有破洞，轉 90° 的倍數、整數平移完全精確；
    /// 任意角度會有鋸齒，關節附近也可能出現縫（相鄰部件在關節處要稍微重疊幾格）。
    /// 轉動方向：標準旋轉矩陣（X → Y → Z 循環，右手定則的數學慣例），在「頂部」視角繞 Z 軸，正角度 = 順時針（跟畫布旋轉選取區一致）。
    /// </summary>
    public static class RigBake
    {
        // 仿射變換：p' = M p + T，12 個數字 = M 按列排（m00 m01 m02 m10 …）+ tx ty tz
        private static double Snap(double v)
        {
            if (Math.Abs(v) < 1e-12) { return 0; }
            if (Math.Abs(v - 1) < 1e-12) { return 1; }
            if (Math.Abs(v + 1) < 1e-12) { return -1; }
            return v;
        }

        internal static double[] Translation(double tx, double ty, double tz)
        {
            return new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, tx, ty, tz };
        }

        /// <summary>繞通過 (cx,cy,cz)、平行於 axis 的軸轉 deg 度。</summary>
        internal static double[] Rotation(BoneAxis axis, double deg, double cx, double cy, double cz)
        {
            double rad = deg * Math.PI / 180.0;
            double c = Snap(Math.Cos(rad)), s = Snap(Math.Sin(rad));
            double[] r;
            switch (axis)
            {
                case BoneAxis.X: r = new double[] { 1, 0, 0, 0, c, -s, 0, s, c }; break;
                case BoneAxis.Y: r = new double[] { c, 0, s, 0, 1, 0, -s, 0, c }; break;
                default: r = new double[] { c, -s, 0, s, c, 0, 0, 0, 1 }; break;
            }
            double[] a = new double[12];
            Array.Copy(r, a, 9);
            // T = center - R * center
            a[9] = cx - (r[0] * cx + r[1] * cy + r[2] * cz);
            a[10] = cy - (r[3] * cx + r[4] * cy + r[5] * cz);
            a[11] = cz - (r[6] * cx + r[7] * cy + r[8] * cz);
            return a;
        }

        /// <summary>a ∘ b：先做 b 再做 a。</summary>
        internal static double[] Compose(double[] a, double[] b)
        {
            double[] r = new double[12];
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    r[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
                }
                r[9 + i] = a[i * 3] * b[9] + a[i * 3 + 1] * b[10] + a[i * 3 + 2] * b[11] + a[9 + i];
            }
            return r;
        }

        /// <summary>剛體變換（旋轉 + 平移）的反變換：M 的轉置，T' = −Mᵀ T。</summary>
        internal static double[] InvertRigid(double[] a)
        {
            double[] r = new double[12];
            for (int i = 0; i < 3; i++) { for (int j = 0; j < 3; j++) { r[i * 3 + j] = a[j * 3 + i]; } }
            for (int i = 0; i < 3; i++) { r[9 + i] = -(r[i * 3] * a[9] + r[i * 3 + 1] * a[10] + r[i * 3 + 2] * a[11]); }
            return r;
        }

        /// <summary>
        /// 每個部件的「正向」姿勢變換 W（鍵 0 = 身體）：W(身體) = 身體平移；W(部件) = W(上一層) ∘ 平移 ∘ 繞自己樞軸轉自己的角度。
        /// pose 為 null 時用骨架上記的目前姿勢。
        /// </summary>
        public static Dictionary<int, double[]> PoseTransforms(Rig rig, Pose pose = null)
        {
            if (pose == null) { pose = Pose.FromRig(rig); }

            Dictionary<int, double[]> result = new Dictionary<int, double[]>();
            result[0] = Translation(pose.BodyX, pose.BodyY, pose.BodyZ);
            foreach (Bone b in rig.Bones) { Resolve(rig, b, pose, result, 0); }
            return result;
        }

        private static double[] Resolve(Rig rig, Bone b, Pose pose, Dictionary<int, double[]> memo, int depth)
        {
            double[] w;
            if (memo.TryGetValue(b.Id, out w)) { return w; }

            BonePose bp = pose.Peek(b.Id);
            double angle = bp != null ? bp.Angle : 0;
            double[] own = Rotation(b.Axis, angle, b.PivotX, b.PivotY, b.PivotZ);
            if (bp != null && (bp.OffsetX != 0 || bp.OffsetY != 0 || bp.OffsetZ != 0)) { own = Compose(Translation(bp.OffsetX, bp.OffsetY, bp.OffsetZ), own); }

            Bone parent = b.ParentId != 0 && depth <= rig.Count ? rig.Find(b.ParentId) : null;   // depth：萬一資料有循環也不會無限遞迴
            w = Compose(parent == null ? memo[0] : Resolve(rig, parent, pose, memo, depth + 1), own);
            memo[b.Id] = w;
            return w;
        }

        /// <summary>烘焙一幀。結果是新的 VoxelModel（沒有部件標記），來源不動。pose 為 null 時用骨架上記的目前姿勢。</summary>
        public static VoxelModel Bake(VoxelModel src, Rig rig, Pose pose = null)
        {
            if (src == null) { throw new ArgumentNullException("src"); }
            if (rig == null) { throw new ArgumentNullException("rig"); }
            if (pose == null) { pose = Pose.FromRig(rig); }

            int w = src.Width, h = src.Height, d = src.Depth, wh = w * h;
            VoxelModel dst = new VoxelModel(w, h, d, src.Palette);
            byte[] s = src.Data, o = dst.Data, parts = src.Parts;

            bool bodyStill = pose.BodyX == 0 && pose.BodyY == 0 && pose.BodyZ == 0;
            if (bodyStill && (parts == null || rig.Count == 0))
            {
                Buffer.BlockCopy(s, 0, o, 0, s.Length);
                return dst;
            }

            bool[] known = new bool[256];
            foreach (Bone b in rig.Bones) { known[b.Id] = true; }

            // 1) 量每個部件在來源裡的範圍（槽 0 = 身體：沒標記、或標記的部件已經不存在）
            int[] minX = new int[256], minY = new int[256], minZ = new int[256], maxX = new int[256], maxY = new int[256], maxZ = new int[256];
            for (int k = 0; k < 256; k++) { minX[k] = minY[k] = minZ[k] = int.MaxValue; maxX[k] = maxY[k] = maxZ[k] = -1; }
            for (int z = 0, i = 0; z < d; z++)
            {
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++, i++)
                    {
                        if (s[i] == 0) { continue; }

                        int p = parts == null ? 0 : parts[i];
                        if (p != 0 && !known[p]) { p = 0; }

                        if (x < minX[p]) { minX[p] = x; }
                        if (y < minY[p]) { minY[p] = y; }
                        if (z < minZ[p]) { minZ[p] = z; }
                        if (x > maxX[p]) { maxX[p] = x; }
                        if (y > maxY[p]) { maxY[p] = y; }
                        if (z > maxZ[p]) { maxZ[p] = z; }
                    }
                }
            }

            // 2) 身體墊底，再來部件依編號由小到大（後面的蓋住前面的）
            Dictionary<int, double[]> xf = PoseTransforms(rig, pose);
            List<int> order = new List<int>();
            order.Add(0);
            List<Bone> ordered = new List<Bone>(rig.Bones);
            ordered.Sort((a, b) => a.Id.CompareTo(b.Id));
            foreach (Bone b in ordered) { order.Add(b.Id); }

            foreach (int id in order)
            {
                if (maxX[id] < 0) { continue; }   // 沒有任何體素

                double[] fwd = xf[id];
                double[] inv = InvertRigid(fwd);

                // 動完之後落在哪個範圍：把來源範圍的 8 個角送過去，再向外多留 1 格
                double lox = double.MaxValue, loy = double.MaxValue, loz = double.MaxValue, hix = double.MinValue, hiy = double.MinValue, hiz = double.MinValue;
                for (int c = 0; c < 8; c++)
                {
                    double px = (c & 1) != 0 ? maxX[id] + 1 : minX[id];
                    double py = (c & 2) != 0 ? maxY[id] + 1 : minY[id];
                    double pz = (c & 4) != 0 ? maxZ[id] + 1 : minZ[id];
                    double qx = fwd[0] * px + fwd[1] * py + fwd[2] * pz + fwd[9];
                    double qy = fwd[3] * px + fwd[4] * py + fwd[5] * pz + fwd[10];
                    double qz = fwd[6] * px + fwd[7] * py + fwd[8] * pz + fwd[11];
                    if (qx < lox) { lox = qx; } if (qx > hix) { hix = qx; }
                    if (qy < loy) { loy = qy; } if (qy > hiy) { hiy = qy; }
                    if (qz < loz) { loz = qz; } if (qz > hiz) { hiz = qz; }
                }
                int x0 = Math.Max(0, (int)Math.Floor(lox) - 1), x1 = Math.Min(w - 1, (int)Math.Ceiling(hix) + 1);
                int y0 = Math.Max(0, (int)Math.Floor(loy) - 1), y1 = Math.Min(h - 1, (int)Math.Ceiling(hiy) + 1);
                int z0 = Math.Max(0, (int)Math.Floor(loz) - 1), z1 = Math.Min(d - 1, (int)Math.Ceiling(hiz) + 1);

                for (int z = z0; z <= z1; z++)
                {
                    double tz = z + 0.5;
                    for (int y = y0; y <= y1; y++)
                    {
                        double ty = y + 0.5;
                        for (int x = x0; x <= x1; x++)
                        {
                            double tx = x + 0.5;
                            double sx = inv[0] * tx + inv[1] * ty + inv[2] * tz + inv[9];
                            double sy = inv[3] * tx + inv[4] * ty + inv[5] * tz + inv[10];
                            double sz = inv[6] * tx + inv[7] * ty + inv[8] * tz + inv[11];
                            int ix = (int)Math.Floor(sx + 1e-9), iy = (int)Math.Floor(sy + 1e-9), iz = (int)Math.Floor(sz + 1e-9);
                            if ((uint)ix >= (uint)w || (uint)iy >= (uint)h || (uint)iz >= (uint)d) { continue; }

                            int si = iz * wh + iy * w + ix;
                            byte v = s[si];
                            if (v == 0) { continue; }

                            int p = parts == null ? 0 : parts[si];
                            if (p != 0 && !known[p]) { p = 0; }
                            if (p == id) { o[z * wh + y * w + x] = v; }
                        }
                    }
                }
            }
            return dst;
        }

        /// <summary>某個部件目前在這一幀裡的範圍（含頭尾的體素座標）；沒有任何體素回傳 false。</summary>
        public static bool PartBounds(VoxelModel m, int id, out int minX, out int minY, out int minZ, out int maxX, out int maxY, out int maxZ)
        {
            minX = minY = minZ = int.MaxValue;
            maxX = maxY = maxZ = -1;
            if (m.Parts == null) { return false; }

            int w = m.Width, h = m.Height, d = m.Depth;
            for (int z = 0, i = 0; z < d; z++)
            {
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++, i++)
                    {
                        if (m.Parts[i] != id || m.Data[i] == 0) { continue; }

                        if (x < minX) { minX = x; } if (x > maxX) { maxX = x; }
                        if (y < minY) { minY = y; } if (y > maxY) { maxY = y; }
                        if (z < minZ) { minZ = z; } if (z > maxZ) { maxZ = z; }
                    }
                }
            }
            return maxX >= 0;
        }
    }
}
