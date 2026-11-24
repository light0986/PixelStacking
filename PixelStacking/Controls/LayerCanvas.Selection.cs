using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelStacking.Data;

namespace PixelStacking.Controls
{
    /// <summary>
    /// 畫布的「選取」功能（像小畫家）：矩形選取、魔術棒選取 + 複製／剪下／貼上 + 拖曳搬移。
    ///
    /// 選取範圍 = 畫布上的區域 × 厚度，厚度沿用筆刷深度（<see cref="BrushSettings.Depth"/>，受目前切片限制）：
    /// 深度 1 只選目前這一層，深度 N 選目前層加上往後面的 N−1 層（每一層用同樣的區域）。
    /// 區域可以是矩形，也可以是魔術棒選出來的不規則形狀（用「外框矩形 + 遮罩」表示，遮罩外的格子不屬於選取）。
    ///
    /// 「浮動選取」：貼上的內容、或從選取區拖出來的內容，在放下（<see cref="CommitFloating"/>）之前都還沒寫進模型，
    /// 只是蓋在畫布上的一層；所以可以隨便拖、用方向鍵微調、切換切片（內容會跟著目前切片，等於在層與層之間搬移），
    /// Esc 取消就跟沒發生過一樣。放下時才一次寫入（拖出來的搬移會連同清掉原位置），整個動作算一次復原。
    /// </summary>
    public sealed partial class LayerCanvas
    {
        private static readonly Pen AntsBlack = MakeDashPen(Colors.Black, 0);
        private static readonly Pen AntsWhite = MakeDashPen(Colors.White, 3);

        // 選取（畫布格座標；外框矩形一定在畫布範圍內）
        private bool _hasSel;
        private int _selX, _selY, _selW, _selH;
        private bool[] _selMask;                // 外框內哪些格子被選到（長度 _selW*_selH）；null = 整個矩形
        private byte[] _selWeights;             // 羽化：外框內每格被選取的程度 1~255（255 = 完全選到）；null = 沒有羽化
        private bool _selDragging, _selMoved;
        private int _selAnchorX, _selAnchorY;

        // 浮動選取
        private PixelBlock _float;
        private int _floatX, _floatY;           // 左上角的畫布格座標（可以超出畫布，放下時超出的部分會被裁掉）
        private bool _floatLifted;              // true = 從選取區拖出來的（原位置要在放下時才清掉）
        private WriteableBitmap _floatBmp;
        private PixelBlock _floatBase;          // 旋轉前的原始浮動內容（每次旋轉都從它重算，連轉很多次也不會越轉越糊）
        private double _floatAngle;             // 目前相對於 _floatBase 轉了幾度（順時針為正，0 ≤ 值 < 360）
        private double _rotFracX, _rotFracY;    // 真正的旋轉中心跟「整格位置算出來的中心」差多少（轉到奇偶不同的大小時整格取整會差半格；記下來才不會越轉越漂）
        private bool _floatDragging;
        private int _dragOffX, _dragOffY;

        // 拖出來的內容原本在哪裡（放下時要清掉）
        private VoxelModel _liftModel;
        private ViewDirection _liftView;
        private int _liftSlice, _liftDepth, _liftX, _liftY, _liftW, _liftH;
        private bool[] _liftMask;
        private byte[] _liftWeights;

        private bool _pasteKeepTransparent;

        // 不規則選取的外緣線（快取：遮罩或格子大小沒變就不用重算）
        private bool[] _outlineSelMask; private double _outlineSelScale; private Geometry _outlineSel;
        private bool[] _outlineFloatMask; private double _outlineFloatScale; private Geometry _outlineFloat;

        /// <summary>
        /// 放下浮動選取時，內容裡的透明格要不要「蓋掉」底下原本的東西。true = 不蓋（只放有顏色的格子，小畫家的「透明選取」）。
        /// 注意：魔術棒選出來的區域，遮罩外的格子不管這個選項都不會動。
        /// </summary>
        public bool PasteKeepTransparent
        {
            get => _pasteKeepTransparent;
            set
            {
                if (_pasteKeepTransparent == value) { return; }

                _pasteKeepTransparent = value;
                if (_float != null) { BuildFloatBitmap(); InvalidateVisual(); }
            }
        }

        /// <summary>
        /// 魔術棒的範圍：false（預設）= 只選「跟點到的格子相連」的同色區域；true = 全域同色，選目前這一層裡所有跟它同色的格子（不要求相連）。
        /// </summary>
        public bool WandGlobal { get; set; }

        /// <summary>
        /// 魔術棒的容許誤差（0~255）：0（預設）= 只選跟點到的格子「完全同色」；大於 0 = 顏色的 R、G、B、A 四個通道都跟它相差不超過這個值就算同色。
        /// 透明格只跟透明格算同色（不論誤差多大），有顏色的格子不會把透明格選進來。
        /// </summary>
        public int WandTolerance { get; set; }

        /// <summary>
        /// 魔術棒的羽化（0~8 格）：0（預設）= 硬邊。大於 0 = 選取區「往內」N 格的邊緣漸變：最外圈只選到 1/(N+1)，往內逐圈增加，再往內完全選到
        /// （選取框範圍不變；畫布邊界不算邊緣）。被選到的程度（權重）會跟著選取區走：
        /// 刪除時邊緣只淡掉一部分（透明度乘上 1−權重）、複製／剪下／搬移／貼上時邊緣以權重混進底下原有的顏色。
        /// 權重在選取當下（魔術棒點下去、Shift／Ctrl 加減之後）依最後的選取形狀算好，之後再拉滑桿不會改動已經選好的區域。
        /// </summary>
        public int WandFeather { get; set; }

        /// <summary>
        /// 羽化混色用：給一個 ARGB，回傳調色盤裡對應的索引（有一模一樣的就用，沒有就佔用空格；全滿時由外面決定怎麼辦）。
        /// 沒有設定時退回「只在現有調色盤裡找最接近的顏色」。
        /// </summary>
        public Func<int, byte> ColorIndexProvider { get; set; }

        /// <summary>選取狀態（有沒有選取、浮動內容的位置大小…）改變時觸發，外面用來更新提示文字。</summary>
        public event Action SelectionChanged;

        public bool HasSelection => _hasSel || _float != null;
        public bool HasFloating => _float != null;

        /// <summary>目前是不是用選取類工具（矩形選取或魔術棒）。</summary>
        private bool IsSelectionTool => _tool == PaintTool.Select || _tool == PaintTool.Wand;

        private static Pen MakeDashPen(Color color, double dashOffset)
        {
            Pen pen = new Pen(new SolidColorBrush(color), 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, dashOffset) };
            pen.Freeze();
            return pen;
        }

        private void RaiseSelectionChanged()
        {
            Action handler = SelectionChanged;
            if (handler != null) { handler(); }
            InvalidateVisual();
        }

        /// <summary>目前的選取厚度：筆刷深度，但不能超過目前切片往後還剩幾片。</summary>
        public int SelectionDepth => Math.Max(1, Math.Min(Brush.Depth, _slice + 1));

        private static int CountTrue(bool[] mask)
        {
            int n = 0;
            foreach (bool b in mask) { if (b) { n++; } }
            return n;
        }

        /// <summary>給提示文字用的描述；沒有選取時是空字串。</summary>
        public string DescribeSelection()
        {
            if (_float != null)
            {
                string shape = _float.Mask == null ? $"{_float.Width}×{_float.Height}" : LangResources.Format(TextKey.Selection_CellArea, _float.CellCount, _float.Width, _float.Height);
                if (_float.Weights != null) { shape += LangResources.Get(TextKey.LayerCanvas_FeatheredEdge); }
                if (_floatAngle != 0) { shape += LangResources.Format(TextKey.Selection_Rotated, _floatAngle.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)); }
                return LangResources.Format(TextKey.Selection_FloatingDescription, (_floatLifted ? LangResources.Get(TextKey.LayerCanvas_Moving) : LangResources.Get(TextKey.LayerCanvas_Pasting)), shape, _float.Depth)
                    + LangResources.Get(TextKey.LayerCanvas_DragToMoveArrowKeys);
            }
            if (_hasSel)
            {
                string shape = _selMask == null ? $"{_selW}×{_selH}" : LangResources.Format(TextKey.Selection_CellArea, CountTrue(_selMask), _selW, _selH);
                if (_selWeights != null) { shape += LangResources.Get(TextKey.LayerCanvas_FeatheredEdge); }
                return LangResources.Format(TextKey.Selection_Description, shape, SelectionDepth);
            }
            return "";
        }

        // --- 選取區 / 浮動內容的格子判斷 ---------------------------------------------------------------------
        private static bool InRect(int cx, int cy, int x, int y, int w, int h)
        {
            return cx >= x && cx < x + w && cy >= y && cy < y + h;
        }

        private bool InSelection(int cx, int cy)
        {
            if (!_hasSel || !InRect(cx, cy, _selX, _selY, _selW, _selH)) { return false; }
            return _selMask == null || _selMask[(cy - _selY) * _selW + (cx - _selX)];
        }

        private bool InFloat(int cx, int cy)
        {
            if (_float == null || !InRect(cx, cy, _floatX, _floatY, _float.Width, _float.Height)) { return false; }
            return _float.Mask == null || _float.Mask[(cy - _floatY) * _float.Width + (cx - _floatX)];
        }

        // --- 區塊讀寫 ---------------------------------------------------------------------------------------
        private PixelBlock ReadBlock(int x0, int y0, int w, int h, int depth, bool[] mask, byte[] weights)
        {
            PixelBlock block = new PixelBlock(w, h, depth);
            block.Mask = mask;
            block.Weights = weights;
            byte[] data = _model.Data;
            for (int k = 0; k < depth; k++)
            {
                int origin, stepX, stepY;
                ViewMapper.Plane(_view, _slice - k, _model.Width, _model.Height, _model.Depth, out origin, out stepX, out stepY);
                byte[] layer = block.Layers[k];
                for (int j = 0; j < h; j++)
                {
                    for (int i = 0; i < w; i++)
                    {
                        if (mask != null && !mask[j * w + i]) { continue; }   // 遮罩外的格子不屬於這一塊，保持 0
                        layer[j * w + i] = data[origin + (x0 + i) * stepX + (y0 + j) * stepY];
                    }
                }
            }
            return block;
        }

        /// <summary>
        /// 羽化的混色器：把「新內容」依權重混進「底下原本的內容」，回傳調色盤索引（0 = 透明）。
        /// 權重 255 = 完全用新內容，權重越小底下越多；兩邊都用「透明度加權」平均 R、G、B（透明的一方不貢獻顏色，只貢獻透明度）。
        /// 同一組 (新, 舊, 權重) 的結果會快取，不會重複佔用調色盤格。
        /// </summary>
        private sealed class WeightMixer
        {
            private readonly int[] _pal;
            private readonly Func<int, byte> _resolve;
            private readonly Dictionary<int, byte> _cache = new Dictionary<int, byte>();

            public WeightMixer(int[] palette, Func<int, byte> resolve)
            {
                _pal = palette;
                _resolve = resolve ?? (argb => NearestIndex(palette, argb));
            }

            public byte Mix(byte src, byte dst, int w)
            {
                if (w >= 255 || src == dst) { return src; }
                if (w <= 0) { return dst; }

                int key = src | (dst << 8) | (w << 16);
                byte hit;
                if (_cache.TryGetValue(key, out hit)) { return hit; }

                int cs = src == 0 ? 0 : _pal[src], cd = dst == 0 ? 0 : _pal[dst];
                double asrc = ((cs >> 24) & 0xFF) * (double)w;            // 新內容的「份量」= 透明度 × 權重
                double adst = ((cd >> 24) & 0xFF) * (double)(255 - w);    // 舊內容的份量
                double sum = asrc + adst;
                byte result = 0;
                int alpha = (int)Math.Round(sum / 255.0);
                if (sum > 0 && alpha > 0)
                {
                    int r = (int)Math.Round((((cs >> 16) & 0xFF) * asrc + ((cd >> 16) & 0xFF) * adst) / sum);
                    int g = (int)Math.Round((((cs >> 8) & 0xFF) * asrc + ((cd >> 8) & 0xFF) * adst) / sum);
                    int b = (int)Math.Round(((cs & 0xFF) * asrc + (cd & 0xFF) * adst) / sum);
                    result = _resolve((alpha << 24) | (r << 16) | (g << 8) | b);
                }
                _cache[key] = result;
                return result;
            }
        }

        /// <summary>調色盤裡跟 argb 最接近的有色格（沒有 provider 時的退路；全部都是空格就回 0）。</summary>
        private static byte NearestIndex(int[] pal, int argb)
        {
            int best = 0;
            long bestDist = long.MaxValue;
            for (int i = 1; i < pal.Length; i++)
            {
                if (((uint)pal[i] >> 24) == 0) { continue; }

                long d = 0;
                for (int sh = 0; sh < 32; sh += 8)
                {
                    long diff = ((argb >> sh) & 255) - ((pal[i] >> sh) & 255);
                    d += diff * diff;
                }
                if (d < bestDist) { bestDist = d; best = i; }
            }
            return (byte)best;
        }

        /// <summary>
        /// 算羽化權重：整張畫布大小的遮罩 → 每格 1~255（255 = 完全選到）。選到的格子離「畫布內沒選到的格子」越近權重越小：
        /// 權重 = min(1, 距離 / (radius+1))，最外圈（距離 1）= 1/(radius+1)。畫布邊界不算邊緣。沒有任何格子需要漸變就回傳 null。
        /// </summary>
        private static byte[] ComputeFeather(bool[] mask, int cw, int ch, int radius)
        {
            if (radius <= 0) { return null; }

            byte[] weights = new byte[cw * ch];
            bool any = false;
            double span = radius + 1;
            for (int y = 0; y < ch; y++)
            {
                for (int x = 0; x < cw; x++)
                {
                    int c = y * cw + x;
                    if (!mask[c]) { continue; }

                    double best = double.MaxValue;
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        int ny = y + dy;
                        if ((uint)ny >= (uint)ch) { continue; }

                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int nx = x + dx;
                            if ((uint)nx >= (uint)cw || mask[ny * cw + nx]) { continue; }

                            double d = Math.Sqrt(dx * dx + dy * dy);
                            if (d < best) { best = d; }
                        }
                    }

                    int w = best >= span ? 255 : (int)Math.Max(1, Math.Round(255.0 * best / span));
                    weights[c] = (byte)w;
                    if (w < 255) { any = true; }
                }
            }
            return any ? weights : null;
        }

        /// <summary>把某個區域（往後 depth 片；有遮罩就只清遮罩內的格子）清成透明，被改的格子的原值記進 old（只記第一次）。有羽化權重的格子只淡掉一部分。</summary>
        private static void ClearRegionInto(Dictionary<int, byte> old, VoxelModel m, ViewDirection view, int slice, int depth,
            int x0, int y0, int w, int h, bool[] mask, byte[] weights, WeightMixer mixer)
        {
            for (int k = 0; k < depth; k++)
            {
                int s = slice - k;
                if (s < 0) { break; }

                int origin, stepX, stepY;
                ViewMapper.Plane(view, s, m.Width, m.Height, m.Depth, out origin, out stepX, out stepY);
                for (int j = 0; j < h; j++)
                {
                    for (int i = 0; i < w; i++)
                    {
                        if (mask != null && !mask[j * w + i]) { continue; }

                        int idx = origin + (x0 + i) * stepX + (y0 + j) * stepY;
                        int wgt = weights == null ? 255 : weights[j * w + i];
                        byte nv = wgt >= 255 ? (byte)0 : mixer.Mix(0, m.Data[idx], wgt);
                        byte was;
                        if (m.SetIndex(idx, nv, out was) && !old.ContainsKey(idx)) { old[idx] = was; }
                    }
                }
            }
        }

        /// <summary>把區塊寫進模型：第 0 層放在 slice，之後往後面；超出畫布或超出第 0 片的部分裁掉；遮罩外的格子不動。</summary>
        private static void WriteBlockInto(Dictionary<int, byte> old, VoxelModel m, ViewDirection view, int slice,
            PixelBlock block, int x0, int y0, bool keepTransparent, WeightMixer mixer)
        {
            byte[] weights = block.Weights;
            int cw = ViewMapper.CanvasWidth(view, m.Width, m.Height, m.Depth);
            int ch = ViewMapper.CanvasHeight(view, m.Width, m.Height, m.Depth);
            int w = block.Width, h = block.Height;
            bool[] mask = block.Mask;

            for (int k = 0; k < block.Depth; k++)
            {
                int s = slice - k;
                if (s < 0) { break; }

                int origin, stepX, stepY;
                ViewMapper.Plane(view, s, m.Width, m.Height, m.Depth, out origin, out stepX, out stepY);
                byte[] layer = block.Layers[k];
                for (int j = 0; j < h; j++)
                {
                    int cy = y0 + j;
                    if ((uint)cy >= (uint)ch) { continue; }

                    for (int i = 0; i < w; i++)
                    {
                        int cx = x0 + i;
                        if ((uint)cx >= (uint)cw) { continue; }
                        if (mask != null && !mask[j * w + i]) { continue; }

                        byte v = layer[j * w + i];
                        if (v == 0 && keepTransparent) { continue; }

                        int idx = origin + cx * stepX + cy * stepY;
                        int wgt = weights == null ? 255 : weights[j * w + i];
                        if (wgt < 255) { v = mixer.Mix(v, m.Data[idx], wgt); }   // 羽化的邊緣：以權重混進底下原有的顏色
                        byte was;
                        if (m.SetIndex(idx, v, out was) && !old.ContainsKey(idx)) { old[idx] = was; }
                    }
                }
            }
        }

        private void RaiseStroke(VoxelModel model, ViewDirection view, int slice, Dictionary<int, byte> old)
        {
            if (old.Count == 0) { return; }

            int[] indices = new int[old.Count];
            byte[] values = new byte[old.Count];
            int n = 0;
            foreach (KeyValuePair<int, byte> kv in old) { indices[n] = kv.Key; values[n] = kv.Value; n++; }
            model.NotifyChanged();
            StrokeEnded?.Invoke(model, view, slice, indices, values);
        }

        // --- 公開的操作 -------------------------------------------------------------------------------------
        /// <summary>全選：選取整個畫布。</summary>
        public void SelectAll()
        {
            if (_model == null) { return; }

            CommitFloating();
            SetSelection(0, 0, CanvasWidth, CanvasHeight);
            RaiseSelectionChanged();
        }

        /// <summary>目前選取（或浮動內容）的區塊；沒有選取時回傳 null。不會改動模型。</summary>
        public PixelBlock CopyBlock()
        {
            if (_float != null) { return _float; }
            if (_hasSel && _model != null) { return ReadBlock(_selX, _selY, _selW, _selH, SelectionDepth, _selMask, _selWeights); }
            return null;
        }

        private WeightMixer NewMixer() { return new WeightMixer(_model.Palette, ColorIndexProvider); }

        /// <summary>
        /// 刪除選取：選取區 → 把該區域（往後深度層）清成透明，算一次復原；浮動內容 → 丟掉
        /// （從選取區拖出來的搬移中內容，原位置也會一併清掉，因為內容被「剪掉」了）。剪下 = 先 <see cref="CopyBlock"/> 再呼叫這個。
        /// </summary>
        public void DeleteSelection()
        {
            if (_model == null) { return; }

            if (_float != null)
            {
                if (_floatLifted && ReferenceEquals(_liftModel, _model) && _liftView == _view)
                {
                    Dictionary<int, byte> old = new Dictionary<int, byte>();
                    ClearRegionInto(old, _model, _liftView, _liftSlice, _liftDepth, _liftX, _liftY, _liftW, _liftH, _liftMask, _liftWeights, NewMixer());
                    DiscardFloat(false);
                    RaiseStroke(_model, _view, _liftSlice, old);
                    Rebuild();
                }
                else { DiscardFloat(false); Rebuild(); }
                RaiseSelectionChanged();
                return;
            }

            if (!_hasSel) { return; }

            Dictionary<int, byte> cleared = new Dictionary<int, byte>();
            ClearRegionInto(cleared, _model, _view, _slice, SelectionDepth, _selX, _selY, _selW, _selH, _selMask, _selWeights, NewMixer());
            RaiseStroke(_model, _view, _slice, cleared);
            RaiseSelectionChanged();
        }

        /// <summary>貼上：把區塊變成浮動選取，出現在畫布左上角，之後可以拖曳；放下前不會改動模型。</summary>
        public void BeginPaste(PixelBlock block)
        {
            if (_model == null || block == null) { return; }

            CommitFloating();
            _hasSel = false;
            _selMask = null; _selWeights = null;
            _float = block;
            _floatBase = null;
            _floatAngle = 0;
            _floatLifted = false;
            _floatX = 0;
            _floatY = 0;
            BuildFloatBitmap();
            RaiseSelectionChanged();
        }

        /// <summary>放下浮動選取：一次寫進目前切片（及往後的層）；從選取區拖出來的搬移會連同清掉原位置，整個算一次復原。</summary>
        public void CommitFloating()
        {
            if (_float == null) { return; }

            PixelBlock block = _float;
            int fx = _floatX, fy = _floatY;
            // 要不要清掉原位置，必須在 DiscardFloat 之前判斷（它會把記錄來源幀的 _liftModel 清成 null）
            bool clearSource = _floatLifted && ReferenceEquals(_liftModel, _model) && _liftView == _view;
            DiscardFloat(false);

            if (_model != null)
            {
                Dictionary<int, byte> old = new Dictionary<int, byte>();
                WeightMixer mixer = NewMixer();
                if (clearSource)
                {
                    ClearRegionInto(old, _model, _liftView, _liftSlice, _liftDepth, _liftX, _liftY, _liftW, _liftH, _liftMask, _liftWeights, mixer);
                }
                WriteBlockInto(old, _model, _view, _slice, block, fx, fy, _pasteKeepTransparent, mixer);
                SetSelectionMask(fx, fy, block.Width, block.Height, block.Mask, block.Weights);   // 放下後內容維持選取（可以再拖、再複製）
                RaiseStroke(_model, _view, _slice, old);
                Rebuild();
            }
            RaiseSelectionChanged();
        }

        /// <summary>取消浮動選取：貼上的內容直接丟掉；拖出來的內容原位置本來就沒動過，所以等於沒發生，選取框回到原處。</summary>
        public void CancelFloating()
        {
            if (_float == null) { return; }

            bool lifted = _floatLifted;
            int lx = _liftX, ly = _liftY, lw = _liftW, lh = _liftH;
            bool[] lmask = _liftMask;
            byte[] lweights = _liftWeights;
            DiscardFloat(false);
            if (lifted) { SetSelectionMask(lx, ly, lw, lh, lmask, lweights); }
            Rebuild();
            RaiseSelectionChanged();
        }

        /// <summary>取消選取（浮動內容會先放下）。</summary>
        public void ClearSelection()
        {
            CommitFloating();
            _hasSel = false;
            _selMask = null; _selWeights = null;
            _selDragging = false;
            RaiseSelectionChanged();
        }

        /// <summary>Esc：有浮動內容就取消它，否則取消選取框。</summary>
        public void CancelSelectionOrFloat()
        {
            if (_float != null) { CancelFloating(); }
            else if (_hasSel) { _hasSel = false; _selMask = null; _selWeights = null; RaiseSelectionChanged(); }
        }

        /// <summary>目前浮動內容相對於原樣轉了幾度（順時針為正；沒轉過是 0）。</summary>
        public double FloatingAngle => _floatAngle;

        /// <summary>
        /// 旋轉選取區（或浮動中的貼上內容）再轉 deltaDegrees 度（順時針為正），繞內容的正中心、沿視線方向（選取的每一層一起轉）。
        /// 只有選取區、還沒浮動時，會先把它拖起來（變成「搬移中」，原位置到放下時才清掉）；之後可以拖曳、再轉、Enter 放下（一次復原）、Esc 取消。
        /// 每次都從轉之前的原樣重算（累計角度），所以轉 +15° 六次跟轉 +90° 一次結果完全一樣，轉回去也會完全還原。
        /// 沒有選取也沒有浮動內容時回傳 false。要繞別的軸轉，換一個視角再轉。
        /// </summary>
        public bool RotateSelection(double deltaDegrees)
        {
            if (_model == null) { return false; }
            if (_float == null)
            {
                if (!_hasSel) { return false; }
                LiftSelection();
            }
            if (_floatBase == null) { _floatBase = _float; _floatAngle = 0; _rotFracX = 0; _rotFracY = 0; }

            double angle = (_floatAngle + deltaDegrees) % 360.0;
            if (angle < 0) { angle += 360.0; }
            if (angle < 1e-9 || angle > 360.0 - 1e-9) { angle = 0; }

            // 保持中心不動：目前這塊（可能已經被拖曳過）的中心 + 上次取整留下的零頭 = 真正的旋轉中心
            double cxc = _floatX + _float.Width / 2.0 + _rotFracX, cyc = _floatY + _float.Height / 2.0 + _rotFracY;
            PixelBlock rotated = BlockRotation.Rotate(_floatBase, angle);
            _float = rotated;
            _floatAngle = angle;
            _floatX = (int)Math.Round(cxc - rotated.Width / 2.0);
            _floatY = (int)Math.Round(cyc - rotated.Height / 2.0);
            _rotFracX = cxc - (_floatX + rotated.Width / 2.0);
            _rotFracY = cyc - (_floatY + rotated.Height / 2.0);
            BuildFloatBitmap();
            RaiseSelectionChanged();
            return true;
        }

        /// <summary>方向鍵微調浮動內容的位置。</summary>
        public void NudgeFloating(int dx, int dy)
        {
            if (_float == null) { return; }

            _floatX += dx;
            _floatY += dy;
            BuildFloatBitmap();
            RaiseSelectionChanged();
        }

        private void DiscardFloat(bool keepSelection)
        {
            _float = null;
            _floatBase = null;
            _floatAngle = 0;
            _floatLifted = false;
            _floatDragging = false;
            _floatBmp = null;
            _liftModel = null;
            if (!keepSelection) { _hasSel = false; _selMask = null; _selWeights = null; }
        }

        /// <summary>換幀、換視角、換工具之前呼叫：放下浮動內容並清掉選取框（選取框是跟著畫布方向的）。</summary>
        private void ResetSelectionState()
        {
            CommitFloating();
            _hasSel = false;
            _selMask = null; _selWeights = null;
            _selDragging = false;
            RaiseSelectionChanged();
        }

        private void SetSelection(int x, int y, int w, int h) { SetSelectionMask(x, y, w, h, null, null); }

        /// <summary>把對應原本外框 w×h 的陣列，裁成裁切後 nw×nh（左上角偏移 offX, offY）。</summary>
        private static T[] CropArray<T>(T[] src, int w, int offX, int offY, int nw, int nh)
        {
            T[] r = new T[nw * nh];
            for (int j = 0; j < nh; j++)
            {
                for (int i = 0; i < nw; i++) { r[j * nw + i] = src[(offY + j) * w + offX + i]; }
            }
            return r;
        }

        /// <summary>設定選取：外框矩形（會裁到畫布範圍內）+ 可選的遮罩與羽化權重（對應原本的外框 w×h；裁切時跟著裁）。</summary>
        private void SetSelectionMask(int x, int y, int w, int h, bool[] mask, byte[] weights)
        {
            int cw = CanvasWidth, ch = CanvasHeight;
            int x0 = Math.Max(0, x), y0 = Math.Max(0, y);
            int x1 = Math.Min(cw, x + w), y1 = Math.Min(ch, y + h);
            if (x1 <= x0 || y1 <= y0) { _hasSel = false; _selMask = null; _selWeights = null; return; }

            int cnw = x1 - x0, cnh = y1 - y0;
            bool whole = cnw == w && cnh == h;
            bool[] cropped = null;
            if (mask != null)
            {
                cropped = whole ? mask : CropArray(mask, w, x0 - x, y0 - y, cnw, cnh);
                if (CountTrue(cropped) == 0) { _hasSel = false; _selMask = null; _selWeights = null; return; }
            }
            byte[] croppedWeights = weights == null ? null : (whole ? weights : CropArray(weights, w, x0 - x, y0 - y, cnw, cnh));

            _hasSel = true;
            _selX = x0; _selY = y0; _selW = cnw; _selH = cnh;
            _selMask = cropped;
            _selWeights = croppedWeights;
        }

        // --- 魔術棒 -----------------------------------------------------------------------------------------
        /// <summary>
        /// 魔術棒：選取目前這一層裡「跟 (cx, cy) 同色、上下左右連通」的區域（跟填滿的區域判斷一樣；點在空白格就是選那一塊連通的空白）；
        /// <see cref="WandGlobal"/> 開啟時改成選這一層裡所有同色的格子（不要求相連）。
        /// 厚度沿用筆刷深度（每一層用同一個區域）。add = 加入目前的選取，subtract = 從目前的選取減去，兩個都不是 = 取代。
        /// </summary>
        public void WandPick(int cx, int cy, bool add, bool subtract)
        {
            if (_model == null) { return; }

            int cw = CanvasWidth, ch = CanvasHeight;
            if ((uint)cx >= (uint)cw || (uint)cy >= (uint)ch) { return; }

            CommitFloating();

            int origin, stepX, stepY;
            ViewMapper.Plane(_view, _slice, _model.Width, _model.Height, _model.Depth, out origin, out stepX, out stepY);
            byte[] data = _model.Data;
            byte target = data[origin + cx * stepX + cy * stepY];

            bool[] match = BuildMatchTable(_model.Palette, target, WandTolerance);
            bool[] region = WandGlobal
                ? GlobalMask(data, origin, stepX, stepY, cw, ch, match)
                : FloodMask(data, origin, stepX, stepY, cw, ch, cx, cy, match);
            bool[] result = region;
            if (add || subtract)
            {
                bool[] current = SelectionAsCanvasMask(cw, ch);
                result = new bool[cw * ch];
                for (int i = 0; i < result.Length; i++) { result[i] = add ? (current[i] || region[i]) : (current[i] && !region[i]); }
            }

            ApplyCanvasMask(result, cw, ch, WandFeather);
            RaiseSelectionChanged();
        }

        /// <summary>
        /// 預先算好「調色盤編號 i 算不算跟 target 同色」的對照表（256 格），掃描時只要查表。
        /// 誤差 0：只有編號相同才算；誤差 &gt; 0：非透明的兩個顏色四個通道最大差距 ≤ 誤差；透明（編號 0）只跟透明相符。
        /// </summary>
        private static bool[] BuildMatchTable(int[] palette, byte target, int tolerance)
        {
            bool[] match = new bool[256];
            match[target] = true;
            if (tolerance <= 0 || target == 0 || palette == null || target >= palette.Length) { return match; }

            int t = palette[target];
            for (int i = 1; i < 256 && i < palette.Length; i++)
            {
                int c = palette[i];
                int da = Math.Abs(((c >> 24) & 0xFF) - ((t >> 24) & 0xFF));
                int dr = Math.Abs(((c >> 16) & 0xFF) - ((t >> 16) & 0xFF));
                int dg = Math.Abs(((c >> 8) & 0xFF) - ((t >> 8) & 0xFF));
                int db = Math.Abs((c & 0xFF) - (t & 0xFF));
                match[i] = Math.Max(Math.Max(da, dr), Math.Max(dg, db)) <= tolerance;
            }
            return match;
        }

        /// <summary>找出從 (cx,cy) 出發、上下左右四向連通、顏色符合對照表的畫布格，回傳整張畫布大小的遮罩。</summary>
        private static bool[] FloodMask(byte[] data, int origin, int stepX, int stepY, int cw, int ch, int cx, int cy, bool[] match)
        {
            bool[] region = new bool[cw * ch];
            bool[] seen = new bool[cw * ch];
            Stack<int> stack = new Stack<int>();
            stack.Push(cy * cw + cx);
            seen[cy * cw + cx] = true;

            while (stack.Count > 0)
            {
                int c = stack.Pop();
                int x = c % cw, y = c / cw;
                if (!match[data[origin + x * stepX + y * stepY]]) { continue; }

                region[c] = true;
                if (x > 0 && !seen[c - 1]) { seen[c - 1] = true; stack.Push(c - 1); }
                if (x < cw - 1 && !seen[c + 1]) { seen[c + 1] = true; stack.Push(c + 1); }
                if (y > 0 && !seen[c - cw]) { seen[c - cw] = true; stack.Push(c - cw); }
                if (y < ch - 1 && !seen[c + cw]) { seen[c + cw] = true; stack.Push(c + cw); }
            }
            return region;
        }

        /// <summary>全域同色：目前這一層裡所有顏色符合對照表的畫布格（不要求相連），回傳整張畫布大小的遮罩。</summary>
        private static bool[] GlobalMask(byte[] data, int origin, int stepX, int stepY, int cw, int ch, bool[] match)
        {
            bool[] region = new bool[cw * ch];
            for (int y = 0; y < ch; y++)
            {
                for (int x = 0; x < cw; x++) { region[y * cw + x] = match[data[origin + x * stepX + y * stepY]]; }
            }
            return region;
        }

        /// <summary>目前的選取（矩形或遮罩）展開成整張畫布大小的遮罩；沒有選取就是全 false。</summary>
        private bool[] SelectionAsCanvasMask(int cw, int ch)
        {
            bool[] m = new bool[cw * ch];
            if (!_hasSel) { return m; }

            for (int j = 0; j < _selH; j++)
            {
                for (int i = 0; i < _selW; i++)
                {
                    if (_selMask == null || _selMask[j * _selW + i]) { m[(_selY + j) * cw + _selX + i] = true; }
                }
            }
            return m;
        }

        /// <summary>把整張畫布大小的遮罩設成目前的選取：算出外框矩形並壓縮成外框大小的遮罩；整個外框都選到就退回矩形選取。</summary>
        private void ApplyCanvasMask(bool[] canvasMask, int cw, int ch, int feather)
        {
            int minX = cw, minY = ch, maxX = -1, maxY = -1;
            for (int y = 0; y < ch; y++)
            {
                for (int x = 0; x < cw; x++)
                {
                    if (!canvasMask[y * cw + x]) { continue; }
                    if (x < minX) { minX = x; }
                    if (x > maxX) { maxX = x; }
                    if (y < minY) { minY = y; }
                    if (y > maxY) { maxY = y; }
                }
            }

            if (maxX < 0) { _hasSel = false; _selMask = null; _selWeights = null; return; }

            int w = maxX - minX + 1, h = maxY - minY + 1;
            bool[] mask = new bool[w * h];
            int count = 0;
            for (int j = 0; j < h; j++)
            {
                for (int i = 0; i < w; i++)
                {
                    bool b = canvasMask[(minY + j) * cw + minX + i];
                    mask[j * w + i] = b;
                    if (b) { count++; }
                }
            }

            // 羽化權重依「最後的選取形狀」重算（加選／減選之後也是），再裁成外框大小
            byte[] canvasWeights = ComputeFeather(canvasMask, cw, ch, feather);
            byte[] weights = canvasWeights == null ? null : CropArray(canvasWeights, cw, minX, minY, w, h);

            _hasSel = true;
            _selX = minX; _selY = minY; _selW = w; _selH = h;
            _selMask = count == w * h ? null : mask;
            _selWeights = weights;
        }

        // --- 滑鼠（選取工具 / 魔術棒）-----------------------------------------------------------------------
        private void SelectMouseDown(int cx, int cy)
        {
            if (_float != null)
            {
                if (InFloat(cx, cy))
                {
                    _floatDragging = true;
                    _dragOffX = cx - _floatX;
                    _dragOffY = cy - _floatY;
                    return;
                }
                CommitFloating();   // 點在框外：先放下，接著當作開始新的選取
            }

            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool wandCombine = _tool == PaintTool.Wand && (shift || ctrl);

            // 點在選取區裡面（不規則區域就必須點在被選到的格子上）：把內容拖起來。魔術棒按著 Shift / Ctrl 時是加減選取，不拖
            if (!wandCombine && InSelection(cx, cy))
            {
                LiftSelection();
                _floatDragging = true;
                _dragOffX = cx - _floatX;
                _dragOffY = cy - _floatY;
                return;
            }

            if (_tool == PaintTool.Wand)
            {
                WandPick(cx, cy, shift, ctrl && !shift);
                return;
            }

            _selDragging = true;
            _selMoved = false;
            _selAnchorX = Math.Max(0, Math.Min(CanvasWidth - 1, cx));
            _selAnchorY = Math.Max(0, Math.Min(CanvasHeight - 1, cy));
            SetSelection(_selAnchorX, _selAnchorY, 1, 1);
            RaiseSelectionChanged();
        }

        private void SelectMouseMove(int cx, int cy)
        {
            if (_floatDragging && _float != null)
            {
                int nx = cx - _dragOffX, ny = cy - _dragOffY;
                if (nx != _floatX || ny != _floatY)
                {
                    _floatX = nx;
                    _floatY = ny;
                    BuildFloatBitmap();
                    RaiseSelectionChanged();
                }
                return;
            }

            if (_selDragging)
            {
                int x = Math.Max(0, Math.Min(CanvasWidth - 1, cx));
                int y = Math.Max(0, Math.Min(CanvasHeight - 1, cy));
                if (x != _selAnchorX || y != _selAnchorY) { _selMoved = true; }

                SetSelection(Math.Min(_selAnchorX, x), Math.Min(_selAnchorY, y), Math.Abs(x - _selAnchorX) + 1, Math.Abs(y - _selAnchorY) + 1);
                RaiseSelectionChanged();
            }
        }

        private void EndSelectDrag()
        {
            if (_selDragging)
            {
                _selDragging = false;
                if (!_selMoved)
                {
                    _hasSel = false;   // 只點一下沒拖 = 取消選取（跟小畫家一樣）
                    _selMask = null; _selWeights = null;
                    RaiseSelectionChanged();
                }
            }
            _floatDragging = false;
        }

        /// <summary>把選取區拖起來：內容讀進浮動區塊，原位置在放下之前都不動（畫面上暫時顯示成空的）。</summary>
        private void LiftSelection()
        {
            PixelBlock block = ReadBlock(_selX, _selY, _selW, _selH, SelectionDepth, _selMask, _selWeights);
            _liftWeights = _selWeights;
            _liftModel = _model;
            _liftView = _view;
            _liftSlice = _slice;
            _liftDepth = block.Depth;
            _liftX = _selX; _liftY = _selY; _liftW = _selW; _liftH = _selH;
            _liftMask = _selMask;

            _float = block;
            _floatBase = null;
            _floatAngle = 0;
            _floatLifted = true;
            _floatX = _selX;
            _floatY = _selY;
            _hasSel = false;
            _selMask = null; _selWeights = null;
            BuildFloatBitmap();
            Rebuild();   // 套用「原位置暫時顯示成空的」
            RaiseSelectionChanged();
        }

        /// <summary>目前顯示的這一片，原本被拖起來的那塊內容所在的矩形（暫時顯示成空的；mask 非 null 時只有遮罩內的格子）。</summary>
        private bool TryGetHiddenRegion(out int x0, out int y0, out int x1, out int y1, out bool[] mask)
        {
            x0 = y0 = x1 = y1 = 0;
            mask = null;
            if (_float == null || !_floatLifted || !ReferenceEquals(_liftModel, _model) || _liftView != _view) { return false; }
            if (_slice > _liftSlice || _slice <= _liftSlice - _liftDepth) { return false; }

            x0 = _liftX; y0 = _liftY; x1 = _liftX + _liftW; y1 = _liftY + _liftH;
            mask = _liftMask;
            return true;
        }

        // --- 畫出來 -----------------------------------------------------------------------------------------
        /// <summary>浮動內容的第 0 層（= 目前切片看到的那層）轉成一張小圖，疊在畫布上。</summary>
        private void BuildFloatBitmap()
        {
            if (_float == null || _model == null) { _floatBmp = null; return; }

            int w = _float.Width, h = _float.Height;
            int[] px = new int[w * h];
            int[] pal = _model.Palette;
            byte[] layer = _float.Layers[0];
            bool[] mask = _float.Mask;
            byte[] weights = _float.Weights;
            for (int j = 0; j < h; j++)
            {
                for (int i = 0; i < w; i++)
                {
                    if (mask != null && !mask[j * w + i]) { px[j * w + i] = 0; continue; }   // 不屬於這一塊：完全透明

                    byte v = layer[j * w + i];
                    if (v != 0)
                    {
                        int argb = pal[v];
                        if (weights != null && weights[j * w + i] < 255)   // 羽化的邊緣：透明度乘上權重（跟放下時混色的結果相近）
                        {
                            int a = (int)Math.Round(((argb >> 24) & 0xFF) * weights[j * w + i] / 255.0);
                            argb = (a << 24) | (argb & 0xFFFFFF);
                        }
                        px[j * w + i] = argb;
                    }
                    else if (_pasteKeepTransparent) { px[j * w + i] = 0; }   // 透明：底下的東西會透出來
                    else { px[j * w + i] = ((_floatX + i + _floatY + j) & 1) == 0 ? CheckA : CheckB; }   // 會蓋掉底下 → 顯示成空白棋盤
                }
            }

            if (_floatBmp == null || _floatBmp.PixelWidth != w || _floatBmp.PixelHeight != h)
            {
                _floatBmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            }
            _floatBmp.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
        }

        /// <summary>不規則區域的外緣線：把遮罩邊界上連續的格邊合併成長線段（虛線才會連續），座標以區域左上角為原點、單位是像素。</summary>
        private static Geometry BuildOutline(bool[] mask, int w, int h, double scale)
        {
            StreamGeometry geo = new StreamGeometry();
            using (StreamGeometryContext ctx = geo.Open())
            {
                Func<int, int, bool> on = (x, y) => x >= 0 && y >= 0 && x < w && y < h && mask[y * w + x];

                // 水平邊：第 y 條格線上，上方沒有選到而下方有（上緣），或下方沒有而上方有（下緣）
                for (int y = 0; y <= h; y++)
                {
                    int runStart = -1;
                    for (int x = 0; x <= w; x++)
                    {
                        bool edge = x < w && (on(x, y) != on(x, y - 1));
                        if (edge && runStart < 0) { runStart = x; }
                        if (!edge && runStart >= 0)
                        {
                            ctx.BeginFigure(new Point(runStart * scale, y * scale), false, false);
                            ctx.LineTo(new Point(x * scale, y * scale), true, false);
                            runStart = -1;
                        }
                    }
                }
                // 垂直邊
                for (int x = 0; x <= w; x++)
                {
                    int runStart = -1;
                    for (int y = 0; y <= h; y++)
                    {
                        bool edge = y < h && (on(x, y) != on(x - 1, y));
                        if (edge && runStart < 0) { runStart = y; }
                        if (!edge && runStart >= 0)
                        {
                            ctx.BeginFigure(new Point(x * scale, runStart * scale), false, false);
                            ctx.LineTo(new Point(x * scale, y * scale), true, false);
                            runStart = -1;
                        }
                    }
                }
            }
            geo.Freeze();
            return geo;
        }

        private void DrawAnts(DrawingContext dc, Rect r, bool[] mask, int w, int h, bool forFloat)
        {
            if (mask == null)
            {
                // 矩形：螞蟻線＝黑白兩條錯開的虛線，不管底下什麼顏色都看得到
                dc.DrawRectangle(null, AntsBlack, r);
                dc.DrawRectangle(null, AntsWhite, r);
                return;
            }

            Geometry geo;
            if (forFloat)
            {
                if (!ReferenceEquals(_outlineFloatMask, mask) || _outlineFloatScale != _scale) { _outlineFloat = BuildOutline(mask, w, h, _scale); _outlineFloatMask = mask; _outlineFloatScale = _scale; }
                geo = _outlineFloat;
            }
            else
            {
                if (!ReferenceEquals(_outlineSelMask, mask) || _outlineSelScale != _scale) { _outlineSel = BuildOutline(mask, w, h, _scale); _outlineSelMask = mask; _outlineSelScale = _scale; }
                geo = _outlineSel;
            }

            dc.PushTransform(new TranslateTransform(r.X, r.Y));
            dc.DrawGeometry(null, AntsBlack, geo);
            dc.DrawGeometry(null, AntsWhite, geo);
            dc.Pop();
        }

        private void DrawSelectionOverlay(DrawingContext dc, Rect canvasRect)
        {
            Rect r;
            if (_float != null)
            {
                r = new Rect(canvasRect.X + _floatX * _scale, canvasRect.Y + _floatY * _scale, _float.Width * _scale, _float.Height * _scale);
                if (_floatBmp != null)
                {
                    dc.PushClip(new RectangleGeometry(canvasRect));
                    dc.DrawImage(_floatBmp, r);

                    // 浮動內容是一張沒有格線的小圖，蓋住了底下畫布的格線，所以同樣的格線要再畫一次，看起來才跟畫布一致
                    if (_showGrid && _scale >= 8)
                    {
                        for (int x = 0; x <= _float.Width; x++)
                        {
                            double px = r.X + x * _scale;
                            dc.DrawLine(GridPen, new Point(px, r.Y), new Point(px, r.Bottom));
                        }
                        for (int y = 0; y <= _float.Height; y++)
                        {
                            double py = r.Y + y * _scale;
                            dc.DrawLine(GridPen, new Point(r.X, py), new Point(r.Right, py));
                        }
                    }
                    dc.Pop();
                }
                DrawAnts(dc, r, _float.Mask, _float.Width, _float.Height, true);
            }
            else if (_hasSel)
            {
                r = new Rect(canvasRect.X + _selX * _scale, canvasRect.Y + _selY * _scale, _selW * _scale, _selH * _scale);
                DrawAnts(dc, r, _selMask, _selW, _selH, false);
            }
        }
    }
}
