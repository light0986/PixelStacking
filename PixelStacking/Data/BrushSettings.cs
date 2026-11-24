using System;

namespace PixelStacking.Data
{
    /// <summary>
    /// 筆刷形狀：方形／圓形是畫布上的平面形狀（粗細 = 邊長／直徑，再用「筆刷深度」往後面疊幾層）；
    /// 立方體／膠囊型／橢圓球／圓柱體／圓錐體／金字塔／圓環／螺旋／彈簧是真正的立體筆刷（立體範圍由粗細決定，見 <see cref="BrushSettings"/>）。
    /// </summary>
    public enum BrushShape { Square, Circle, Cube, Capsule, Ellipsoid, Cylinder, Cone, Pyramid, Torus, Helix, Spring }

    /// <summary>
    /// 筆刷形狀與粗細（格數）。以滑鼠所在格為中心，偶數粗細時中心偏向左上（立體筆刷也偏向後面）。
    ///
    /// 立體座標用「相對於點到的格子與目前切片」的三個偏移 (dx, dy, dz) 表示，dz &gt; 0 = 往前面（靠近觀看者、切片編號變大），dz &lt; 0 = 往後面。
    /// <list type="bullet">
    /// <item><description>方形／圓形：footprint 在畫布上，dz = 0 往後 <see cref="Depth"/>−1 層（每一層同一個形狀）。</description></item>
    /// <item><description>立方體：粗細 = 邊長，三個方向都是 Size 格，以點到的格子與目前層為中心。不使用深度。</description></item>
    /// <item><description>膠囊型：粗細 = 半徑，軸沿視線方向（垂直於畫布），軸向總長 = max(深度, 2×半徑+1)（含兩端的半球，深度不夠長時就是球）；以點到的格子與目前層為中心。</description></item>
    /// <item><description>橢圓球：粗細 = 畫布上（左右、上下）的半徑，視線方向的總長 = 深度（半軸 = 深度/2）；以點到的格子與目前層為中心。深度 = 2×半徑+1 時就是球，更短是扁的、更長是拉長的橄欖球形。</description></item>
    /// <item><description>圓柱體：粗細 = 半徑，軸沿視線方向，長度 = 深度（每一層都是同樣的圓盤，兩端是平的）；以點到的格子與目前層為中心。</description></item>
    /// <item><description>圓錐體：粗細 = 底面半徑，軸沿視線方向，長度 = 深度；底面在最後面（切片編號最小的那一層，半徑最大），尖端朝向觀看者（半徑逐層線性縮小，最前面一層只剩中心附近）。以點到的格子與目前層為中心。</description></item>
    /// <item><description>金字塔：粗細 = 正方形底面的邊長，軸沿視線方向，長度 = 深度；底面在最後面、尖端朝向觀看者（邊長逐層線性縮小，最前面一層至少剩中心一格或 2×2）。</description></item>
    /// <item><description>圓環（甜甜圈）：粗細 = 環的半徑（圓心到管子中心線），軸沿視線方向（環平躺在畫布上），管子的粗細（管徑）= 深度，也就是沿視線方向厚 Depth 層；點到的格子是環的正中央（洞）。深度 1 = 一圈平的環。</description></item>
    /// <item><description>螺旋：粗細 = 螺旋的半徑，軸沿視線方向，總長 = 深度，每 (半徑+4) 層轉一圈；線是 1 個體素粗、連續不斷（26 連通）；起點在最後面那一層的右邊（+x），隨著往前面（切片編號變大）在畫布上順時針轉。以點到的格子為軸、目前層為中心。</description></item>
    /// <item><description>彈簧：跟螺旋同一條螺旋線，但線是粗的（半徑 1 的小球沿著螺旋掃過去，線徑 3 格），所以螺距拉開成每 (半徑+6) 層轉一圈，圈與圈之間才不會黏在一起。粗細 = 彈簧（螺旋線）的半徑，總長 = 深度（兩端被切齊，不會比深度更長）。</description></item>
    /// </list>
    /// </summary>
    public sealed class BrushSettings
    {
        public BrushShape Shape { get; set; } = BrushShape.Square;

        private int _size = 1;
        public int Size
        {
            get => _size;
            set => _size = Math.Max(1, Math.Min(32, value));
        }

        private int _depth = 1;

        /// <summary>
        /// 筆刷深度（層數）：方形／圓形 = 1 只畫目前這一層、N ≥ 2 = 同時畫目前這一層，以及沿視線方向往「後面」（遠離觀看者、切片編號變小）的 N−1 層，
        /// 到第一層為止（不會超出模型）；膠囊型 = 軸向總長；立方體不使用。填滿與選取也用它當厚度。
        /// </summary>
        public int Depth
        {
            get => _depth;
            set => _depth = Math.Max(1, Math.Min(MaxDepth, value));
        }

        public const int MaxDepth = 128;

        /// <summary>是不是立體筆刷（立方體或膠囊型）。</summary>
        public bool Is3D => Shape == BrushShape.Cube || Shape == BrushShape.Capsule || Shape == BrushShape.Ellipsoid || Shape == BrushShape.Cylinder || Shape == BrushShape.Cone || Shape == BrushShape.Pyramid || Shape == BrushShape.Torus || Shape == BrushShape.Helix || Shape == BrushShape.Spring;

        /// <summary>膠囊型的軸向總長（格）：深度不夠長（小於球的直徑 2×半徑+1）時就是一顆球。</summary>
        public int CapsuleLength => Math.Max(_depth, 2 * _size + 1);

        /// <summary>這支筆刷在視線方向（dz）涵蓋的範圍 [min, max]，一定包含 0。</summary>
        public void DzRange(out int min, out int max)
        {
            switch (Shape)
            {
                case BrushShape.Cube:
                    min = -(_size / 2);
                    max = min + _size - 1;
                    break;
                case BrushShape.Ellipsoid:
                case BrushShape.Cylinder:
                case BrushShape.Cone:
                case BrushShape.Pyramid:
                case BrushShape.Torus:
                case BrushShape.Helix:
                case BrushShape.Spring:
                    min = -(_depth / 2);
                    max = min + _depth - 1;
                    break;
                case BrushShape.Capsule:
                    min = -(CapsuleLength / 2);
                    max = min + CapsuleLength - 1;
                    break;
                default:
                    min = -(_depth - 1);
                    max = 0;
                    break;
            }
        }

        /// <summary>
        /// 對筆刷涵蓋的每一個體素呼叫 action(畫布 x, 畫布 y, dz)（座標可能超出模型範圍，由呼叫端判斷）。
        /// </summary>
        public void ForEachVoxel(int cx, int cy, Action<int, int, int> action)
        {
            int s = _size;

            if (Shape == BrushShape.Spring)
            {
                foreach (int[] o in SpringOffsets()) { action(cx + o[0], cy + o[1], o[2]); }
                return;
            }

            if (Shape == BrushShape.Helix)
            {
                foreach (int[] o in HelixOffsets()) { action(cx + o[0], cy + o[1], o[2]); }
                return;
            }

            if (Shape == BrushShape.Torus)
            {
                int tMin, tMax;
                DzRange(out tMin, out tMax);
                for (int dz = tMin; dz <= tMax; dz++) { TorusLayer(cx, cy, dz, action); }
                return;
            }

            if (Shape == BrushShape.Pyramid)
            {
                int pMin, pMax;
                DzRange(out pMin, out pMax);
                for (int dz = pMin; dz <= pMax; dz++) { PyramidLayer(cx, cy, dz, action); }
                return;
            }

            if (Shape == BrushShape.Cone)
            {
                int kMin, kMax;
                DzRange(out kMin, out kMax);
                for (int dz = kMin; dz <= kMax; dz++) { ConeLayer(cx, cy, dz, action); }
                return;
            }

            if (Shape == BrushShape.Cylinder)
            {
                int cMin, cMax;
                DzRange(out cMin, out cMax);
                for (int dz = cMin; dz <= cMax; dz++) { DiscLayer(cx, cy, dz, action); }
                return;
            }

            if (Shape == BrushShape.Ellipsoid)
            {
                int eMin, eMax;
                DzRange(out eMin, out eMax);
                for (int dz = eMin; dz <= eMax; dz++) { EllipsoidLayer(cx, cy, dz, action); }
                return;
            }

            if (Shape == BrushShape.Capsule)
            {
                int capMin, capMax;
                DzRange(out capMin, out capMax);
                for (int dz = capMin; dz <= capMax; dz++) { CapsuleLayer(cx, cy, dz, action); }
                return;
            }

            // 方形／圓形／立方體：每一層都是同一個平面形狀（立方體的平面形狀就是方形）
            int min, max;
            DzRange(out min, out max);
            ForEachFootprint(cx, cy, (x, y) =>
            {
                for (int dz = min; dz <= max; dz++) { action(x, y, dz); }
            });
        }

        /// <summary>
        /// 膠囊型在視線方向偏移 dz 的那一層的每一格：半徑 = Size；兩個半球的球心在軸上 c1、c2，
        /// 離軸上線段 [c1, c2] 的距離 ≤ 半徑+0.5 的格子都算（dz 超出膠囊範圍時不會有任何格子）。
        /// </summary>
        private void CapsuleLayer(int cx, int cy, int dz, Action<int, int, int> action)
        {
            int s = _size;
            int length = CapsuleLength;
            int lo = -(length / 2);
            int c1 = lo + s, c2 = lo + length - 1 - s;
            int t = Math.Max(c1, Math.Min(c2, dz));
            double az = (dz - t) * (double)(dz - t);
            double limit = (s + 0.5) * (s + 0.5);
            for (int dy = -s; dy <= s; dy++)
            {
                for (int dx = -s; dx <= s; dx++)
                {
                    if (dx * dx + dy * dy + az <= limit) { action(cx + dx, cy + dy, dz); }
                }
            }
        }

        /// <summary>螺旋每轉一圈要幾層（螺距）：半徑 + 4。</summary>
        public int HelixPitch => _size + 4;

        private int _helixSize = -1, _helixDepth = -1;
        private System.Collections.Generic.List<int[]> _helixCache;

        /// <summary>
        /// 螺旋的所有體素偏移 {dx, dy, dz}（相對於點到的格子與目前切片，已去掉重複）；半徑與深度沒變就直接用快取。
        /// 沿著螺旋線密密地取樣（每步在平面上與沿軸都不超過半格），每個取樣點四捨五入到所在的格子，所以線是連續的（26 連通）。
        /// </summary>
        private System.Collections.Generic.List<int[]> HelixOffsets()
        {
            if (_helixCache != null && _helixSize == _size && _helixDepth == _depth) { return _helixCache; }

            _helixCache = CoilPath(HelixPitch);
            _helixSize = _size;
            _helixDepth = _depth;
            return _helixCache;
        }

        /// <summary>彈簧每轉一圈要幾層（螺距）：半徑 + 6（線比較粗，圈要拉開一點）。</summary>
        public int SpringPitch => _size + 6;

        private int _springSize = -1, _springDepth = -1;
        private System.Collections.Generic.List<int[]> _springCache;

        /// <summary>
        /// 彈簧的所有體素偏移：螺距 <see cref="SpringPitch"/> 的螺旋線，每個點都膨脹成半徑 1 的小球（19 格），
        /// 再把超出總長（深度）範圍的切掉，去掉重複。
        /// </summary>
        private System.Collections.Generic.List<int[]> SpringOffsets()
        {
            if (_springCache != null && _springSize == _size && _springDepth == _depth) { return _springCache; }

            int lo = -(_depth / 2), hi = lo + _depth - 1;
            var seen = new System.Collections.Generic.HashSet<long>();
            var list = new System.Collections.Generic.List<int[]>();
            foreach (int[] p in CoilPath(SpringPitch))
            {
                for (int a = -1; a <= 1; a++)
                {
                    for (int b = -1; b <= 1; b++)
                    {
                        for (int c = -1; c <= 1; c++)
                        {
                            if (a * a + b * b + c * c > 2) { continue; }

                            int dx = p[0] + a, dy = p[1] + b, dz = p[2] + c;
                            if (dz < lo || dz > hi) { continue; }

                            long key = ((long)(dx + 1024) << 40) | ((long)(dy + 1024) << 20) | (long)(dz + 1024);
                            if (seen.Add(key)) { list.Add(new[] { dx, dy, dz }); }
                        }
                    }
                }
            }

            _springSize = _size;
            _springDepth = _depth;
            _springCache = list;
            return list;
        }

        /// <summary>螺旋線本身（1 個體素粗）的取樣結果，螺距由呼叫端指定。</summary>
        private System.Collections.Generic.List<int[]> CoilPath(double pitch)
        {
            int length = _depth;
            int lo = -(length / 2);
            double step = Math.Min(0.5, 0.5 * pitch / (2 * Math.PI * _size));
            var seen = new System.Collections.Generic.HashSet<long>();
            var list = new System.Collections.Generic.List<int[]>();
            for (double sPos = lo - 0.5; sPos < lo + length - 0.5 - 1e-9; sPos += step)
            {
                double theta = 2 * Math.PI * (sPos - lo) / pitch;
                int dx = (int)Math.Round(_size * Math.Cos(theta), MidpointRounding.AwayFromZero);
                int dy = (int)Math.Round(_size * Math.Sin(theta), MidpointRounding.AwayFromZero);
                int dz = (int)Math.Floor(sPos + 0.5);
                if (dz < lo || dz > lo + length - 1) { continue; }

                long key = ((long)(dx + 1024) << 40) | ((long)(dy + 1024) << 20) | (long)(dz + 1024);
                if (seen.Add(key)) { list.Add(new[] { dx, dy, dz }); }
            }
            return list;
        }

        /// <summary>
        /// 圓環的一層（視線方向偏移 dz）：環半徑 R = Size，管半徑 tr = 深度/2，管子中心線在軸向範圍的正中央（深度為偶數時落在兩層之間）。
        /// 格子到環中心線圓的距離 √((ρ−R)² + (dz−中心)²) ≤ tr 才算（ρ = 離中心的平面距離）。
        /// </summary>
        private void TorusLayer(int cx, int cy, int dz, Action<int, int, int> action)
        {
            int ring = _size;
            int lo = -(_depth / 2);
            double center = lo + (_depth - 1) / 2.0;
            double tr = _depth / 2.0;
            double u = (dz - center) * (dz - center);
            double rest = tr * tr - u;
            if (rest < 0) { return; }

            double reach = Math.Sqrt(rest);
            int w = (int)Math.Ceiling(ring + tr);
            for (int dy = -w; dy <= w; dy++)
            {
                for (int dx = -w; dx <= w; dx++)
                {
                    double rho = Math.Sqrt(dx * dx + dy * dy);
                    if (Math.Abs(rho - ring) <= reach + 1e-9) { action(cx + dx, cy + dy, dz); }
                }
            }
        }

        /// <summary>
        /// 金字塔的一層：從最後面的那一層（底面）算起第 i 層（0 = 底面），正方形的半邊長 = (Size/2) × (1 − i/長度)，
        /// 以底面的中心線為準（偶數邊長時中心落在兩格之間），跟立方體的取格方式一致；邊長是偶數時至少保留中心的 2×2。
        /// </summary>
        private void PyramidLayer(int cx, int cy, int dz, Action<int, int, int> action)
        {
            int s = _size;
            int lo = -(s / 2);
            int zlo = -(_depth / 2);
            int i = dz - zlo;
            if (i < 0 || i >= _depth) { return; }

            double center = lo + (s - 1) / 2.0;
            double half = Math.Max(s / 2.0 * (1.0 - (double)i / _depth), s % 2 == 0 ? 0.5 : 0.0);
            for (int j = 0; j < s; j++)
            {
                if (Math.Abs(lo + j - center) > half + 1e-9) { continue; }
                for (int k = 0; k < s; k++)
                {
                    if (Math.Abs(lo + k - center) <= half + 1e-9) { action(cx + lo + k, cy + lo + j, dz); }
                }
            }
        }

        /// <summary>
        /// 圓錐體的一層：從最後面的那一層（底面）算起第 i 層（0 = 底面），圓盤半徑 = (Size+0.5) × (1 − i/長度)，
        /// 所以底面是完整的圓盤、最前面一層（i = 長度−1）只剩半徑 (Size+0.5)/長度 的小圓（至少中心那一格）。
        /// </summary>
        private void ConeLayer(int cx, int cy, int dz, Action<int, int, int> action)
        {
            int s = _size;
            int lo = -(_depth / 2);
            int i = dz - lo;
            if (i < 0 || i >= _depth) { return; }

            double r = (s + 0.5) * (1.0 - (double)i / _depth);
            double limit = r * r;
            for (int dy = -s; dy <= s; dy++)
            {
                for (int dx = -s; dx <= s; dx++)
                {
                    if (dx * dx + dy * dy <= limit) { action(cx + dx, cy + dy, dz); }
                }
            }
        }

        /// <summary>圓柱體的一層：半徑 = Size 的圓盤（跟球的赤道一樣，距離 ≤ 半徑+0.5 的格子都算）。</summary>
        private void DiscLayer(int cx, int cy, int dz, Action<int, int, int> action)
        {
            int s = _size;
            double limit = (s + 0.5) * (s + 0.5);
            for (int dy = -s; dy <= s; dy++)
            {
                for (int dx = -s; dx <= s; dx++)
                {
                    if (dx * dx + dy * dy <= limit) { action(cx + dx, cy + dy, dz); }
                }
            }
        }

        /// <summary>
        /// 橢圓球在視線方向偏移 dz 的那一層的每一格：畫布上半徑 = Size（+0.5，跟球一致），視線方向半軸 = 深度/2，
        /// 中心在軸向範圍的正中央（深度為偶數時落在兩層之間）。滿足 (dx²+dy²)/(r+0.5)² + (dz−中心)²/半軸² ≤ 1 的格子才算。
        /// </summary>
        private void EllipsoidLayer(int cx, int cy, int dz, Action<int, int, int> action)
        {
            int s = _size;
            int lo = -(_depth / 2);
            double center = lo + (_depth - 1) / 2.0;
            double semiZ = _depth / 2.0;
            double rxy = s + 0.5;
            double zt = (dz - center) / semiZ;
            double rest = 1 - zt * zt;
            if (rest < 0) { return; }

            double limit = rest * rxy * rxy;
            for (int dy = -s; dy <= s; dy++)
            {
                for (int dx = -s; dx <= s; dx++)
                {
                    if (dx * dx + dy * dy <= limit) { action(cx + dx, cy + dy, dz); }
                }
            }
        }

        /// <summary>平面形狀（方形／圓形；立方體的每一層是方形）的每一格。</summary>
        private void ForEachFootprint(int cx, int cy, Action<int, int> action)
        {
            int s = _size;
            int lo = -(s / 2);
            double center = lo + (s - 1) / 2.0;
            double r = s / 2.0 - 0.25;
            double r2 = r * r;

            for (int j = 0; j < s; j++)
            {
                for (int i = 0; i < s; i++)
                {
                    if (Shape == BrushShape.Circle)
                    {
                        double dx = (lo + i) - center;
                        double dy = (lo + j) - center;
                        if (dx * dx + dy * dy > r2) { continue; }
                    }
                    action(cx + lo + i, cy + lo + j);
                }
            }
        }

        /// <summary>對筆刷在「目前這一層」（dz = 0）的剖面的每一格呼叫 action（滑過時的游標框用；可能超出模型範圍）。</summary>
        public void ForEachCell(int cx, int cy, Action<int, int> action)
        {
            if (Shape == BrushShape.Spring) { foreach (int[] o in SpringOffsets()) { if (o[2] == 0) { action(cx + o[0], cy + o[1]); } } }
            else if (Shape == BrushShape.Helix) { foreach (int[] o in HelixOffsets()) { if (o[2] == 0) { action(cx + o[0], cy + o[1]); } } }
            else if (Shape == BrushShape.Torus) { TorusLayer(cx, cy, 0, (x, y, dz) => action(x, y)); }
            else if (Shape == BrushShape.Pyramid) { PyramidLayer(cx, cy, 0, (x, y, dz) => action(x, y)); }
            else if (Shape == BrushShape.Cone) { ConeLayer(cx, cy, 0, (x, y, dz) => action(x, y)); }
            else if (Shape == BrushShape.Cylinder) { DiscLayer(cx, cy, 0, (x, y, dz) => action(x, y)); }
            else if (Shape == BrushShape.Ellipsoid) { EllipsoidLayer(cx, cy, 0, (x, y, dz) => action(x, y)); }
            else if (Shape == BrushShape.Capsule) { CapsuleLayer(cx, cy, 0, (x, y, dz) => action(x, y)); }
            else { ForEachFootprint(cx, cy, action); }
        }
    }
}
