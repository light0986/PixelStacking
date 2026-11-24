using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelStacking.Data;

namespace PixelStacking.Render
{
    public enum RenderMode { Voxel, Stacked }

    /// <summary>
    /// 預覽：把 VoxelModel 直接在 CPU 算成一張 WriteableBitmap。每個輸出像素各算一次（反向取樣），
    /// 每個像素只寫一次，所以可以依輸出列平行處理。兩種方式（<see cref="RenderMode"/>）：
    /// 實心體素（預設）＝對每個像素發視線，第一個碰到的不透明格子就是它的顏色；層疊＝把每一層當平面疊起來。
    /// 視角：yaw = 繞垂直軸旋轉；elevation = 俯角（90° 正上方俯視、越小越接近側視；實心體素模式可到 -90° 從正下方往上看）。
    /// </summary>
    public sealed class StackRenderer
    {
        private const double MinElevation = 0.17;   // 約 10°，避免壓扁係數趨近 0

        private int _cfgW = -1, _cfgH = -1, _cfgD = -1, _cfgTarget = -1, _cfgScale = -1, _cfgMargin = -1;
        private int[] _buf;
        private int _outW, _outH, _scale;

        // 輪廓線用的輔助緩衝（只有開了面折線才有）：每個像素第一個看到的格子的朝向（1~6，0 = 沒東西）與深度
        private byte[] _face;
        private float[] _depth;
        private byte[] _cov, _mask;

        /// <summary>
        /// 輪廓線（null＝不畫）。外輪廓會長到模型外面，所以要在 Configure／ConfigureScale 之前設好，輸出才會多留空間；
        /// 之後可以隨時換設定（預覽用 <see cref="Margin"/> 固定留好最大的空間，換線寬不用重配）。
        /// </summary>
        public OutlineOptions Outline { get; set; }

        /// <summary>
        /// 輸出四周額外留的空白像素數。-1（預設）= 依 <see cref="Outline"/> 的外輪廓線寬決定（沒開外輪廓就是 0，輸出尺寸跟以前完全一樣）。
        /// </summary>
        public int Margin { get; set; } = -1;

        private int EffectiveMargin { get { return Margin >= 0 ? Margin : (Outline != null ? Outline.Room : 0); } }

        public WriteableBitmap Bitmap { get; private set; }

        /// <summary>
        /// false = 不建立也不寫 WPF 的 <see cref="Bitmap"/>，只算進 <see cref="Pixels"/>（匯出用：WriteableBitmap 綁定建立它的執行緒，
        /// 不能在背景執行緒寫）。必須在 Configure 之前設定。
        /// </summary>
        public bool BitmapEnabled { get; set; } = true;

        /// <summary>最近一次 Render 的輸出像素（非預乘 ARGB，<see cref="OutputWidth"/> × <see cref="OutputHeight"/>，會在下一次 Render 被覆蓋）。</summary>
        public int[] Pixels { get { return _buf; } }
        public int OutputWidth { get { return _outW; } }
        public int OutputHeight { get { return _outH; } }

        /// <summary>每個模型格子在輸出裡佔幾個像素（整數）。</summary>
        public int Scale { get { return _scale; } }

        /// <summary>
        /// 依模型尺寸配置輸出緩衝與 Bitmap（尺寸沒變就什麼都不做，所以同一動畫換幀不會重配、Bitmap 物件維持不變）。
        /// <paramref name="targetSize"/> = 輸出最大邊的目標像素數（預覽 320；縮圖用小一點）。
        /// </summary>
        public void Configure(int width, int height, int depth, int targetSize = 320)
        {
            Setup(width, height, depth, targetSize, 0);
        }

        /// <summary>跟 <see cref="Configure"/> 一樣，但直接指定整數倍率（每個格子幾個像素），匯出用。</summary>
        public void ConfigureScale(int width, int height, int depth, int scale)
        {
            Setup(width, height, depth, 0, Math.Max(1, scale));
        }

        private void Setup(int width, int height, int depth, int targetSize, int fixedScale)
        {
            int margin = EffectiveMargin;
            if (width == _cfgW && height == _cfgH && depth == _cfgD && targetSize == _cfgTarget && fixedScale == _cfgScale && margin == _cfgMargin) { return; }
            _cfgW = width; _cfgH = height; _cfgD = depth; _cfgTarget = targetSize; _cfgScale = fixedScale; _cfgMargin = margin;

            // 輸出最大範圍：旋轉後的對角線寬，高度 = 對角線（俯角 90° 時最高）+ 層數（俯角很小時疊起來的高度）
            int diag = (int)Math.Ceiling(Math.Sqrt((double)width * width + (double)height * height));
            int baseW = diag + 2;
            int baseH = diag + depth + 2;

            // 輸出放大倍率：讓小模型也有夠細的取樣
            _scale = fixedScale > 0 ? fixedScale : Math.Max(1, targetSize / Math.Max(baseW, baseH));
            _outW = baseW * _scale + 2 * margin;
            _outH = baseH * _scale + 2 * margin;

            _buf = new int[_outW * _outH];
            _face = null; _depth = null; _cov = null; _mask = null;
            Bitmap = BitmapEnabled ? new WriteableBitmap(_outW, _outH, 96, 96, PixelFormats.Bgra32, null) : null;
        }

        /// <summary>預覽方式：<see cref="RenderMode.Voxel"/>（預設，實心體素光線投射）或 <see cref="RenderMode.Stacked"/>（層疊）。</summary>
        public RenderMode Mode { get; set; } = RenderMode.Voxel;

        /// <summary>
        /// 模型座標（格子單位，x、y、z 可以是小數；0..寬 / 0..高 / 0..深）換算成最近一次輸出的像素座標，
        /// 用的投影跟 <see cref="Render"/> 完全一樣（依目前的 <see cref="Mode"/>）。預覽上的疊圖（座標軸、編輯層框）用它。
        /// </summary>
        public Point Project(double x, double y, double z, double yaw, double elevation, int width, int height, int depth)
        {
            double s = _scale, ox = _outW / 2.0, oy = _outH / 2.0;
            double px = x - width / 2.0, py = y - height / 2.0;
            if (Mode == RenderMode.Stacked)
            {
                if (elevation < MinElevation) { elevation = MinElevation; }
                if (elevation > Math.PI / 2) { elevation = Math.PI / 2; }
                double cos = Math.Cos(yaw), sin = Math.Sin(yaw);
                double tilt = Math.Sin(elevation), step = Math.Cos(elevation);
                double u = px * cos - py * sin, v = px * sin + py * cos;
                return new Point(ox + u * s, oy + depth * step * s / 2.0 + (v * tilt - z * step) * s);
            }

            if (elevation < -Math.PI / 2) { elevation = -Math.PI / 2; }
            if (elevation > Math.PI / 2) { elevation = Math.PI / 2; }
            double sinY = Math.Sin(yaw), cosY = Math.Cos(yaw), sinE = Math.Sin(elevation), cosE = Math.Cos(elevation);
            double pz = z - depth / 2.0;
            double sx = px * cosY - py * sinY;
            double sy = px * (-sinE * sinY) + py * (-sinE * cosY) + pz * cosE;
            return new Point(ox + sx * s, oy - sy * s);
        }

        /// <param name="m">要畫的那一幀；尺寸必須跟 <see cref="Configure"/> 的一致。</param>
        /// <param name="clip">只畫這個範圍內的格子（含頭尾，超出模型的部分自動收斂）；範圍外的當作不存在，用來「切開」看內部。</param>
        /// <param name="shade">實心體素模式：依面向調整明暗（頂面最亮），讓立體感明顯；層疊模式：依高度把下層調暗。</param>
        public void Render(VoxelModel m, double yaw, double elevation, ClipRange clip, bool shade)
        {
            if (m == null || m.Width != _cfgW || m.Height != _cfgH || m.Depth != _cfgD) { return; }

            if (Mode == RenderMode.Stacked) { RenderStacked(m, yaw, elevation, clip, shade); }
            else { RenderVoxel(m, yaw, elevation, clip, shade); }
        }

        // ============================================================================================
        // 實心體素：每個輸出像素發一條平行視線（正交投影），用 DDA（Amanatides–Woo）逐格前進，
        // 碰到的第一個不透明格子就是這個像素的顏色——不分層，任何角度（含從下方往上看）都是看到什麼顯示什麼。
        // 座標：X 東、Y 南（頂部視角往下）、Z 上；yaw 繞 Z 軸旋轉，elevation = 俯角（+90° 正上方、-90° 正下方）。
        // ============================================================================================
        private const int ShadeTop = 256, ShadeSideX = 214, ShadeSideY = 176, ShadeBottom = 140;

        // --- 半透明合成（兩種預覽方式共用）-----------------------------------------------------------------
        // 視線由前往後走，每碰到一個有顏色的格子就把它的顏色依「還剩多少透明度」混進去：
        // trans = 尚未被擋住的比例（256 = 全透、0 = 全擋）；acc* = 已混入的顏色 × 256。
        // 完全不透明（alpha 255）的格子會直接把 trans 歸零，結果跟以前的「第一個碰到的格子」逐位元相同。

        /// <summary>色彩的 RGB 乘上明暗係數 f（/256），alpha 不變。</summary>
        private static int Shaded(int c, int f)
        {
            if (f == 256) { return c; }
            int r = ((c >> 16) & 255) * f >> 8;
            int g = ((c >> 8) & 255) * f >> 8;
            int b = (c & 255) * f >> 8;
            return (c & unchecked((int)0xFF000000)) | (r << 16) | (g << 8) | b;
        }

        /// <summary>輪廓線需要的暫存緩衝（第一次用到、或輸出尺寸換了才配）。</summary>
        private void EnsureOutlineBuffers(bool withFaces)
        {
            int n = _buf.Length;
            if (_cov == null || _cov.Length != n) { _cov = new byte[n]; _mask = new byte[n]; }
            if (withFaces && (_face == null || _face.Length != n)) { _face = new byte[n]; _depth = new float[n]; }
        }

        /// <summary>渲染完成後加輪廓線（x0..x1、y0..y1 = 這次有重算的範圍，範圍外都是透明）。</summary>
        private void ApplyOutline(int x0, int y0, int x1, int y1, bool withFaces)
        {
            OutlineOptions o = Outline;
            if (o == null || !o.Any) { return; }

            EnsureOutlineBuffers(withFaces);
            OutlinePass.Apply(_buf, _outW, _outH, withFaces ? _face : null, withFaces ? _depth : null, o, x0, y0, x1, y1, _cov, _mask);
        }

        /// <summary>混入一個取樣；回傳 true = 已經幾乎完全擋住，後面不用再走了。</summary>
        private static bool Accumulate(int c, ref int accR, ref int accG, ref int accB, ref int trans)
        {
            int a = (int)((uint)c >> 24);
            if (a == 0) { return false; }

            int w = trans * (a + (a >> 7)) >> 8;   // alpha 255 → 係數 256，剛好吃掉全部剩餘透明度
            accR += ((c >> 16) & 255) * w;
            accG += ((c >> 8) & 255) * w;
            accB += (c & 255) * w;
            trans -= w;
            return trans <= 2;
        }

        /// <summary>把累積結果轉成（非預乘）ARGB：覆蓋率 = 被擋住的比例，沒有碰到任何東西 = 全透明 0。</summary>
        private static int Resolve(int accR, int accG, int accB, int trans)
        {
            int cover = 256 - trans;
            if (cover <= 0) { return 0; }

            int a = cover > 255 ? 255 : cover;
            return (a << 24) | ((accR / cover) << 16) | ((accG / cover) << 8) | (accB / cover);
        }

        private void RenderVoxel(VoxelModel m, double yaw, double elevation, ClipRange clip, bool shade)
        {
            if (elevation < -Math.PI / 2) { elevation = -Math.PI / 2; }
            if (elevation > Math.PI / 2) { elevation = Math.PI / 2; }

            int W = m.Width, H = m.Height, D = m.Depth;
            byte[] data = m.Data;
            int[] pal = m.Palette;
            int wh = W * H;
            int cx0 = Math.Max(0, clip.XMin), cx1 = Math.Min(W - 1, clip.XMax);
            int cy0 = Math.Max(0, clip.YMin), cy1 = Math.Min(H - 1, clip.YMax);
            int zLo = Math.Max(0, clip.ZMin), zHi = Math.Min(D - 1, clip.ZMax);

            // 範圍內有內容的最小方塊：視線只需要走這個方塊，空白的地方一下就跳過
            int minX = W, maxX = -1, minY = H, maxY = -1, minZ = D, maxZ = -1;
            for (int z = zLo; z <= zHi; z++)
            {
                bool any = false;
                for (int y = cy0; y <= cy1; y++)
                {
                    int row = z * wh + y * W;
                    for (int x = cx0; x <= cx1; x++)
                    {
                        if (data[row + x] == 0) { continue; }
                        any = true;
                        if (x < minX) { minX = x; }
                        if (x > maxX) { maxX = x; }
                        if (y < minY) { minY = y; }
                        if (y > maxY) { maxY = y; }
                    }
                }
                if (!any) { continue; }
                if (z < minZ) { minZ = z; }
                if (z > maxZ) { maxZ = z; }
            }

            int[] buf = _buf;
            Array.Clear(buf, 0, buf.Length);

            // 面折線需要每個像素的朝向與深度（外輪廓只看透明度，不需要）
            bool aux = Outline != null && Outline.Creases;
            if (aux) { EnsureOutlineBuffers(true); Array.Clear(_face, 0, _face.Length); }
            byte[] faceBuf = aux ? _face : null;
            float[] depthBuf = aux ? _depth : null;
            int ox0 = 0, oy0 = 0, ox1 = -1, oy1 = -1;   // 這次有重算的範圍

            if (maxX >= 0)
            {
                double sinY = Math.Sin(yaw), cosY = Math.Cos(yaw);
                double sinE = Math.Sin(elevation), cosE = Math.Cos(elevation);

                // 螢幕右方向 R、螢幕上方向 U、視線方向 V（皆為單位向量，彼此垂直）
                double rx = cosY, ry = -sinY, rz = 0;
                double ux = -sinE * sinY, uy = -sinE * cosY, uz = cosE;
                double vx = -cosE * sinY, vy = -cosE * cosY, vz = -sinE;

                double s = _scale, ox = _outW / 2.0, oy = _outH / 2.0;
                double cxm = W / 2.0, cym = H / 2.0, czm = D / 2.0;
                double bx0 = minX, bx1 = maxX + 1, by0 = minY, by1 = maxY + 1, bz0 = minZ, bz1 = maxZ + 1;

                // 內容方塊的 8 個角投影到螢幕，只重算那個矩形
                double sMinX = double.MaxValue, sMaxX = double.MinValue, sMinY = double.MaxValue, sMaxY = double.MinValue;
                for (int ci = 0; ci < 8; ci++)
                {
                    double px = ((ci & 1) == 0 ? bx0 : bx1) - cxm;
                    double py = ((ci & 2) == 0 ? by0 : by1) - cym;
                    double pz = ((ci & 4) == 0 ? bz0 : bz1) - czm;
                    double X = ox + (px * rx + py * ry + pz * rz) * s;
                    double Y = oy - (px * ux + py * uy + pz * uz) * s;
                    if (X < sMinX) { sMinX = X; }
                    if (X > sMaxX) { sMaxX = X; }
                    if (Y < sMinY) { sMinY = Y; }
                    if (Y > sMaxY) { sMaxY = Y; }
                }

                int pad = _scale + 2;
                int x0 = Math.Max(0, (int)Math.Floor(sMinX) - pad);
                int x1 = Math.Min(_outW - 1, (int)Math.Ceiling(sMaxX) + pad);
                int y0 = Math.Max(0, (int)Math.Floor(sMinY) - pad);
                int y1 = Math.Min(_outH - 1, (int)Math.Ceiling(sMaxY) + pad);

                bool flatX = Math.Abs(vx) < 1e-12, flatY = Math.Abs(vy) < 1e-12, flatZ = Math.Abs(vz) < 1e-12;
                int stX = vx > 0 ? 1 : -1, stY = vy > 0 ? 1 : -1, stZ = vz > 0 ? 1 : -1;
                double tdX = flatX ? double.MaxValue : Math.Abs(1.0 / vx);
                double tdY = flatY ? double.MaxValue : Math.Abs(1.0 / vy);
                double tdZ = flatZ ? double.MaxValue : Math.Abs(1.0 / vz);
                int outW = _outW;

                ox0 = x0; oy0 = y0; ox1 = x1; oy1 = y1;
                if (x1 >= x0 && y1 >= y0)
                {
                    Parallel.For(y0, y1 + 1, dy =>
                    {
                        double syp = (oy - (dy + 0.5)) / s;   // 螢幕上方為正
                        int row = dy * outW;

                        for (int dx = x0; dx <= x1; dx++)
                        {
                            double sxp = (dx + 0.5 - ox) / s;
                            double p0x = cxm + sxp * rx + syp * ux;
                            double p0y = cym + sxp * ry + syp * uy;
                            double p0z = czm + sxp * rz + syp * uz;

                            // 視線 P = P0 + t·V 與內容方塊求交（slab 法），並記下從哪個軸的面進入
                            double tmin = double.MinValue, tmax = double.MaxValue;
                            int enter = -1;
                            bool miss = false;

                            if (flatX) { if (p0x < bx0 || p0x >= bx1) { miss = true; } }
                            else
                            {
                                double t1 = (bx0 - p0x) / vx, t2 = (bx1 - p0x) / vx;
                                double tn = t1 < t2 ? t1 : t2, tf = t1 < t2 ? t2 : t1;
                                if (tn > tmin) { tmin = tn; enter = 0; }
                                if (tf < tmax) { tmax = tf; }
                            }
                            if (flatY) { if (p0y < by0 || p0y >= by1) { miss = true; } }
                            else
                            {
                                double t1 = (by0 - p0y) / vy, t2 = (by1 - p0y) / vy;
                                double tn = t1 < t2 ? t1 : t2, tf = t1 < t2 ? t2 : t1;
                                if (tn > tmin) { tmin = tn; enter = 1; }
                                if (tf < tmax) { tmax = tf; }
                            }
                            if (flatZ) { if (p0z < bz0 || p0z >= bz1) { miss = true; } }
                            else
                            {
                                double t1 = (bz0 - p0z) / vz, t2 = (bz1 - p0z) / vz;
                                double tn = t1 < t2 ? t1 : t2, tf = t1 < t2 ? t2 : t1;
                                if (tn > tmin) { tmin = tn; enter = 2; }
                                if (tf < tmax) { tmax = tf; }
                            }
                            if (miss || tmin > tmax) { continue; }

                            // 進入點所在的格子（夾在方塊範圍內，避免浮點誤差跑出界）
                            double t0 = enter < 0 ? 0 : tmin + 1e-9;
                            double ex = p0x + t0 * vx, ey = p0y + t0 * vy, ez = p0z + t0 * vz;
                            int ix = Math.Max(minX, Math.Min(maxX, (int)Math.Floor(ex)));
                            int iy = Math.Max(minY, Math.Min(maxY, (int)Math.Floor(ey)));
                            int iz = Math.Max(minZ, Math.Min(maxZ, (int)Math.Floor(ez)));

                            double tmX = flatX ? double.MaxValue : t0 + (((vx > 0) ? ix + 1 : ix) - ex) / vx;
                            double tmY = flatY ? double.MaxValue : t0 + (((vy > 0) ? iy + 1 : iy) - ey) / vy;
                            double tmZ = flatZ ? double.MaxValue : t0 + (((vz > 0) ? iz + 1 : iz) - ez) / vz;

                            // 面向（朝向觀看者的那一面）：進入軸 + 視線方向的反向
                            int faceAxis = enter < 0 ? 2 : enter;
                            int faceSign = faceAxis == 0 ? -stX : faceAxis == 1 ? -stY : -stZ;

                            // 半透明：由前往後累積，直到夠不透明（不透明格子一碰到就結束）
                            int accR = 0, accG = 0, accB = 0, trans = 256;
                            double tCur = t0;   // 目前這一格的進入深度（沿視線，單位＝模型格）
                            int fcode = 0;
                            float dHit = 0;

                            while (true)
                            {
                                byte idx = data[iz * wh + iy * W + ix];
                                if (idx != 0)
                                {
                                    int c = pal[idx];
                                    if (faceBuf != null && fcode == 0 && (((uint)c) >> 24) != 0)
                                    {
                                        fcode = faceAxis * 2 + (faceSign > 0 ? 1 : 0) + 1;
                                        dHit = (float)tCur;
                                    }
                                    if (shade)
                                    {
                                        int f = faceAxis == 2 ? (faceSign > 0 ? ShadeTop : ShadeBottom)
                                              : faceAxis == 0 ? ShadeSideX : ShadeSideY;
                                        c = Shaded(c, f);
                                    }
                                    if (Accumulate(c, ref accR, ref accG, ref accB, ref trans)) { break; }
                                }

                                // 前進到下一格（挑最先碰到的格線）
                                if (tmX < tmY && tmX < tmZ)
                                {
                                    tCur = tmX; ix += stX; tmX += tdX; faceAxis = 0; faceSign = -stX;
                                    if (ix < minX || ix > maxX) { break; }
                                }
                                else if (tmY < tmZ)
                                {
                                    tCur = tmY; iy += stY; tmY += tdY; faceAxis = 1; faceSign = -stY;
                                    if (iy < minY || iy > maxY) { break; }
                                }
                                else
                                {
                                    tCur = tmZ; iz += stZ; tmZ += tdZ; faceAxis = 2; faceSign = -stZ;
                                    if (iz < minZ || iz > maxZ) { break; }
                                }
                            }

                            buf[row + dx] = Resolve(accR, accG, accB, trans);
                            if (faceBuf != null) { faceBuf[row + dx] = (byte)fcode; depthBuf[row + dx] = dHit; }
                        }
                    });
                }
            }

            if (ox1 >= ox0) { ApplyOutline(ox0, oy0, ox1, oy1, aux); }
            if (Bitmap != null) { Bitmap.WritePixels(new Int32Rect(0, 0, _outW, _outH), _buf, _outW * 4, 0); }
        }

        // ============================================================================================
        // 層疊（Sprite Stacking 風格）：每一層當成一張平面疊起來，保留給想看層疊效果的人。
        // ============================================================================================
        private void RenderStacked(VoxelModel m, double yaw, double elevation, ClipRange clip, bool shade)
        {
            if (elevation < MinElevation) { elevation = MinElevation; }
            if (elevation > Math.PI / 2) { elevation = Math.PI / 2; }

            int W = m.Width, H = m.Height, D = m.Depth;
            byte[] data = m.Data;
            int[] pal = m.Palette;
            int layerSize = W * H;
            int cx0 = Math.Max(0, clip.XMin), cx1 = Math.Min(W - 1, clip.XMax);
            int cy0 = Math.Max(0, clip.YMin), cy1 = Math.Min(H - 1, clip.YMax);
            int zLo = Math.Max(0, clip.ZMin), zHi = Math.Min(D - 1, clip.ZMax);

            double cos = Math.Cos(yaw), sin = Math.Sin(yaw);
            double tilt = Math.Sin(elevation);   // 平面的垂直壓扁係數
            double step = Math.Cos(elevation);   // 每一層往上偏移（模型像素）
            double k = step / tilt;
            double s = _scale;
            double ox = _outW / 2.0;
            double oy = _outH / 2.0 + D * step * s / 2.0;
            double hw = W / 2.0, hh = H / 2.0;
            int outW = _outW;
            int[] buf = _buf;

            int[] shadeTab = new int[D];
            for (int z = 0; z < D; z++)
            {
                double t = D > 1 ? z / (double)(D - 1) : 1.0;
                shadeTab[z] = shade ? (int)(256 * (0.55 + 0.45 * t)) : 256;
            }

            // --- 預先掃描：哪些層有東西、有東西的範圍多大（空層與空白區域不用取樣）---------------------------
            int[] used = new int[Math.Max(0, zHi - zLo + 1)];   // 有內容的層，由上往下
            int usedCount = 0;
            int minX = W, maxX = -1, minY = H, maxY = -1, zMin = D, zMax = -1;
            for (int z = zHi; z >= zLo; z--)
            {
                int baseIdx = z * layerSize;
                bool any = false;
                for (int y = cy0; y <= cy1; y++)
                {
                    int rowIdx = baseIdx + y * W;
                    for (int x = cx0; x <= cx1; x++)
                    {
                        if (data[rowIdx + x] == 0) { continue; }
                        any = true;
                        if (x < minX) { minX = x; }
                        if (x > maxX) { maxX = x; }
                        if (y < minY) { minY = y; }
                        if (y > maxY) { maxY = y; }
                    }
                }
                if (!any) { continue; }

                used[usedCount++] = z;
                if (z < zMin) { zMin = z; }
                if (z > zMax) { zMax = z; }
            }

            Array.Clear(buf, 0, buf.Length);
            int[] stackedRect = null;

            if (usedCount > 0)
            {
                // 內容範圍的 8 個角投影到螢幕，得到需要重算的輸出矩形（其餘維持透明）
                double bx0 = minX - hw, bx1 = maxX + 1 - hw, by0 = minY - hh, by1 = maxY + 1 - hh;
                double sMinX = double.MaxValue, sMaxX = double.MinValue, sMinY = double.MaxValue, sMaxY = double.MinValue;
                for (int ci = 0; ci < 8; ci++)
                {
                    double px = (ci & 1) == 0 ? bx0 : bx1;
                    double py = (ci & 2) == 0 ? by0 : by1;
                    int cz = (ci & 4) == 0 ? zMin : zMax;
                    double u = px * cos - py * sin;
                    double v = px * sin + py * cos;
                    double X = ox + u * s;
                    double Y = oy + (v * tilt - cz * step) * s;
                    if (X < sMinX) { sMinX = X; }
                    if (X > sMaxX) { sMaxX = X; }
                    if (Y < sMinY) { sMinY = Y; }
                    if (Y > sMaxY) { sMaxY = Y; }
                }

                int pad = _scale + 2;
                int x0 = Math.Max(0, (int)Math.Floor(sMinX) - pad);
                int x1 = Math.Min(outW - 1, (int)Math.Ceiling(sMaxX) + pad);
                int y0 = Math.Max(0, (int)Math.Floor(sMinY) - pad);
                int y1 = Math.Min(_outH - 1, (int)Math.Ceiling(sMaxY) + pad);

                double ksin = k * sin, kcos = k * cos;
                int topUsed = used[0];
                stackedRect = new int[] { x0, y0, x1, y1 };

                if (x1 >= x0 && y1 >= y0)
                {
                    Parallel.For(y0, y1 + 1, dy =>
                    {
                        // 最上面有內容的那一層在這一列的平面 v 座標；往下 n 層 v 會減少 n * k
                        double v0 = ((dy + 0.5 - oy) / s + topUsed * step) / tilt;
                        int row = dy * outW;

                        for (int dx = x0; dx <= x1; dx++)
                        {
                            double u = (dx + 0.5 - ox) / s;
                            double sx0 = u * cos + v0 * sin + hw;
                            double sy0 = -u * sin + v0 * cos + hh;
                            int accR = 0, accG = 0, accB = 0, trans = 256;

                            for (int n = 0; n < usedCount; n++)
                            {
                                int z = used[n];
                                int dz = topUsed - z;
                                int ix = (int)Math.Floor(sx0 - dz * ksin);
                                int iy = (int)Math.Floor(sy0 - dz * kcos);
                                if (ix < cx0 || ix > cx1 || iy < cy0 || iy > cy1) { continue; }

                                byte idx = data[z * layerSize + iy * W + ix];
                                if (idx == 0) { continue; }

                                if (Accumulate(Shaded(pal[idx], shadeTab[z]), ref accR, ref accG, ref accB, ref trans)) { break; }
                            }

                            buf[row + dx] = Resolve(accR, accG, accB, trans);
                        }
                    });
                }
            }

            // 層疊模式沒有「面」的概念：只有外輪廓（面折線略過）
            if (stackedRect != null) { ApplyOutline(stackedRect[0], stackedRect[1], stackedRect[2], stackedRect[3], false); }
            if (Bitmap != null) { Bitmap.WritePixels(new Int32Rect(0, 0, _outW, _outH), _buf, _outW * 4, 0); }
        }
    }
}
