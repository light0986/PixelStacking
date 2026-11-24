using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelStacking.Data;

namespace PixelStacking.Controls
{
    public enum PaintTool { Pencil, Eraser, Fill, Select, Wand, Picker, Line, Rectangle, Ellipse, Wand3D }

    /// <summary>
    /// 中間的編輯畫布：顯示並編輯某一幀（<see cref="Model"/>）從某個視角（<see cref="View"/>）看的「目前切片」。
    /// 頂部／底部看到的切片是一層（Z），正面／背面是 Y 方向的一片，左側／右側是 X 方向的一片
    /// （對應關係見 <see cref="ViewMapper"/>）。左鍵畫、右鍵擦；可選擇用半透明顯示「後面那一片」與上一幀的同一片當對齊參考。
    /// 內容先合成進一張 W×H 的 WriteableBitmap，再用最近鄰放大成整數倍畫出來。
    /// 復原不在這裡管：每筆畫完觸發 <see cref="StrokeEnded"/>（附被改格子的原值），由外面的 UndoStack 記錄。
    /// </summary>
    public sealed partial class LayerCanvas : FrameworkElement
    {
        private const int GhostAlpha = 90;   // 參考層的不透明度（/256）
        private const int BackdropAlpha = 120;   // 其他圖層（背景參考）的不透明度（/256）
        private static readonly int SelectTint = unchecked((int)0xFFFFE040);   // 3D 魔法棒選到的體素染的顏色
        // 透明格的棋盤有淺色（白 / 淺灰，一般繪圖軟體的樣子）與深色（深灰 / 稍淺的深灰）兩種，由 <see cref="LightChecker"/> 決定；
        // 調色盤的色塊、顏色預覽、A 條跟著用同樣兩個顏色（見 ColorPicker.SetLightChecker）。
        private static readonly int LightCheckA = unchecked((int)0xFFFFFFFF);
        private static readonly int LightCheckB = unchecked((int)0xFFCCCCCC);
        private static readonly int DarkCheckA = unchecked((int)0xFF3C3C3C);
        private static readonly int DarkCheckB = unchecked((int)0xFF464646);

        private bool _lightChecker = true;

        /// <summary>透明格用淺色棋盤（預設）還是深色棋盤。換了會立刻重畫（格線的顏色也跟著換，淺色底用中灰線、深色底用白色細線）。</summary>
        public bool LightChecker
        {
            get => _lightChecker;
            set
            {
                if (_lightChecker == value) { return; }

                _lightChecker = value;
                if (_model != null) { Rebuild(); }
                if (_float != null) { BuildFloatBitmap(); }
                InvalidateVisual();
            }
        }

        private int CheckA => _lightChecker ? LightCheckA : DarkCheckA;
        private int CheckB => _lightChecker ? LightCheckB : DarkCheckB;
        private static readonly int PrevFrameTint = unchecked((int)0xFFFF8030);

        // 格線用中灰：淺色棋盤上看得到（比棋盤深一點）、黑色體素上也看得到（比黑色亮一點）
        private static readonly Pen GridPenLight = MakePen(Color.FromArgb(90, 128, 128, 128));
        private static readonly Pen GridPenDark = MakePen(Color.FromArgb(48, 255, 255, 255));
        private Pen GridPen => _lightChecker ? GridPenLight : GridPenDark;
        // 游標框：外黑內白兩圈，不管底下是淺色棋盤還是深色體素都看得見
        private static readonly Pen HoverPen = MakePen(Color.FromArgb(220, 255, 255, 255));
        private static readonly Pen HoverPenOuter = MakePen(Color.FromArgb(230, 0, 0, 0));

        private VoxelModel _model;
        private VoxelModel _prevFrame;
        private ViewDirection _view = ViewDirection.Top;
        private int _slice;
        private WriteableBitmap _bmp;
        private int[] _buf;

        private double _scale = 1;
        private Point _origin;

        // 筆畫進行中：起筆當下的幀 / 視角 / 切片對應，之後就算畫面切走也不會寫到別的地方
        private bool _painting;
        private MouseButton _strokeButton;
        private VoxelModel _strokeModel;
        private ViewDirection _strokeView;
        private int _strokeSlice;
        private int _strokePlaneOrigin, _strokeStepX, _strokeStepY, _strokeCw, _strokeCh;
        private int[] _strokeOrigins;   // 這一筆涵蓋的每一片的平面起點，索引 = dz − _strokeDzMin（dz > 0 往前面、< 0 往後面）；超出模型的片是 int.MinValue
        private int _strokeDzMin;
        private byte _strokeValue;
        private int _lastX, _lastY;
        private bool _stampChanged;
        private Dictionary<int, byte> _strokeOld;

        private int _hoverX = int.MinValue, _hoverY = int.MinValue;

        public BrushSettings Brush { get; } = new BrushSettings();
        private PaintTool _tool = PaintTool.Pencil;

        public PaintTool Tool
        {
            get => _tool;
            set
            {
                if (_tool == value) { return; }

                // 離開選取類工具（矩形選取、魔術棒之間互換不算離開，選取要保留）：浮動內容先放下、選取框取消
                if (IsSelectionTool && value != PaintTool.Select && value != PaintTool.Wand) { ResetSelectionState(); }
                if (_shapeDragging) { ShapeEnd(false); }   // 拖到一半換工具：這個形狀取消
                _tool = value;
                InvalidateVisual();
            }
        }

        /// <summary>目前畫筆顏色的調色盤索引（1~255）。</summary>
        public byte PaintIndex { get; set; } = 1;

        /// <summary>
        /// 有設的話，每次要用畫筆顏色（起筆、填滿）時才呼叫它取得調色盤索引——讓 RGBA 調色盤可以「畫的時候」才去找／新增對應的色格，
        /// 滑桿拖來拖去不會佔用調色盤。沒設就直接用 <see cref="PaintIndex"/>。
        /// </summary>
        public Func<byte> PaintIndexProvider { get; set; }

        private byte CurrentPaintIndex()
        {
            Func<byte> provider = PaintIndexProvider;
            return provider != null ? provider() : PaintIndex;
        }

        /// <summary>
        /// 一筆畫完（或一次填滿）且有改到東西時觸發：(被改的幀, 視角, 切片, 被改格子的線性索引, 每格畫之前的值)。
        /// 兩個陣列等長、一一對應；復原時照著寫回去即可。
        /// </summary>
        public event Action<VoxelModel, ViewDirection, int, int[], byte[]> StrokeEnded;

        /// <summary>
        /// 填滿工具的範圍：false = 只填目前切片裡連通的同色區域；true = 立體填滿，換掉整個體積中（上下左右前後六向）
        /// 連通的同色實心塊（空白格不會被立體填滿，不然會把整個外部空間填滿）。
        /// </summary>
        public bool Fill3D { get; set; }

        /// <summary>滑鼠在畫布上移動時觸發：游標所在的體素座標；不在畫布範圍內時三個值都是 -1。</summary>
        public event Action<int, int, int> HoverVoxel;

        /// <summary>
        /// 吸管工具點到（或按著拖曳掃過）畫布上的一格時觸發：參數是那一格目前的調色盤索引（0 = 透明）。
        /// 只有索引「變了」才觸發，所以拖曳時不會一直重複通知。
        /// </summary>
        public event Action<byte> ColorPicked;

        // --- 鏡像對稱繪製 ------------------------------------------------------------------------------------
        /// <summary>
        /// 鏡像對稱：以「模型正中央」的平面為軸（用模型的 X／Y／Z 軸，所以任何視角都一致）。開啟之後，畫筆、橡皮擦（含立體筆刷）與填滿
        /// 改到的每一格，都會在對稱的位置也改成同樣的值（可以同時開好幾個軸，最多 8 份）。不影響選取、貼上與其他工具。
        /// 一筆畫的過程中不能中途改變（起筆當下就鎖定）。
        /// </summary>
        public bool MirrorX { get; set; }
        public bool MirrorY { get; set; }
        public bool MirrorZ { get; set; }

        public bool MirrorAny => MirrorX || MirrorY || MirrorZ;

        private bool _strokeMx, _strokeMy, _strokeMz;
        private readonly int[] _mirrorBuf = new int[7];

        /// <summary>
        /// 一個線性索引在目前開啟的鏡像軸下的「其他」對稱位置（不含自己），寫進 buf 回傳個數（最多 7）。
        /// 模型正中央為軸：x → W−1−x（奇數尺寸時正中間那一排鏡像到自己，會被去掉）。
        /// </summary>
        internal static int MirrorIndices(int index, int w, int h, int d, bool mx, bool my, bool mz, int[] buf)
        {
            int x, y, z;
            ViewMapper.ToVoxel(index, w, h, out x, out y, out z);

            int n = 0;
            for (int mask = 1; mask < 8; mask++)
            {
                if (((mask & 1) != 0 && !mx) || ((mask & 2) != 0 && !my) || ((mask & 4) != 0 && !mz)) { continue; }

                int xx = (mask & 1) != 0 ? w - 1 - x : x;
                int yy = (mask & 2) != 0 ? h - 1 - y : y;
                int zz = (mask & 4) != 0 ? d - 1 - z : z;
                int j = zz * w * h + yy * w + xx;
                if (j == index) { continue; }

                bool dup = false;
                for (int k = 0; k < n; k++) { if (buf[k] == j) { dup = true; break; } }
                if (!dup) { buf[n++] = j; }
            }
            return n;
        }

        /// <summary>目前視角的畫布橫軸／縱軸分別是模型的哪一軸（0 = X、1 = Y、2 = Z），以及視線方向是哪一軸。</summary>
        private void CanvasAxes(out int axisX, out int axisY, out int sight)
        {
            switch (_view)
            {
                case ViewDirection.Top:
                case ViewDirection.Bottom: axisX = 0; axisY = 1; sight = 2; break;
                case ViewDirection.Front:
                case ViewDirection.Back: axisX = 0; axisY = 2; sight = 1; break;
                default: axisX = 1; axisY = 2; sight = 0; break;   // 左／右
            }
        }

        private bool MirrorOnAxis(int axis) { return axis == 0 ? MirrorX : axis == 1 ? MirrorY : MirrorZ; }

        // --- 形狀工具：直線／矩形／橢圓 -----------------------------------------------------------------------
        /// <summary>
        /// 形狀工具用法：按下設起點、按著拖曳（畫布上即時預覽）、放開才真的畫下去，整個形狀算一次復原。左鍵畫目前顏色、右鍵清成透明。
        /// 形狀是沿著路徑「蓋筆刷」畫出來的，所以筆刷形狀／粗細／深度（含立體筆刷）與鏡像對稱全部照常作用。
        /// 矩形與橢圓預設只畫外框，<see cref="ShapeFilled"/> 開啟時畫實心。拖曳時按住 Shift：直線限制成水平／垂直／45°，矩形變正方形、橢圓變正圓。
        /// 拖曳到畫布外會被夾回畫布邊緣；拖曳中換層／換視角／換幀、或按 Esc，這一筆取消。
        /// </summary>
        public bool ShapeFilled { get; set; }

        private bool _shapeDragging;
        private int _shapeX0, _shapeY0, _shapeX1, _shapeY1;
        private MouseButton _shapeButton;
        private ViewDirection _shapeView;
        private int _shapeSlice;
        private VoxelModel _shapeModel;

        private bool IsShapeTool => _tool == PaintTool.Line || _tool == PaintTool.Rectangle || _tool == PaintTool.Ellipse;

        public bool IsShapeDragging => _shapeDragging;

        private static int ClampInt(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }

        internal void ShapeBegin(int cx, int cy, MouseButton button)
        {
            if (_model == null) { return; }

            _shapeX0 = _shapeX1 = ClampInt(cx, 0, CanvasWidth - 1);
            _shapeY0 = _shapeY1 = ClampInt(cy, 0, CanvasHeight - 1);
            _shapeButton = button;
            _shapeView = _view;
            _shapeSlice = _slice;
            _shapeModel = _model;
            _shapeDragging = true;
            InvalidateVisual();
        }

        /// <summary>拖曳到 (cx, cy)：終點夾回畫布；constrain（Shift）時直線吸附到 45° 的倍數、矩形／橢圓變成正方形／正圓。</summary>
        internal void ShapeMove(int cx, int cy, bool constrain)
        {
            if (!_shapeDragging) { return; }

            int cw = CanvasWidth, ch = CanvasHeight;
            int x = ClampInt(cx, 0, cw - 1), y = ClampInt(cy, 0, ch - 1);
            if (constrain)
            {
                int dx = x - _shapeX0, dy = y - _shapeY0;
                int adx = Math.Abs(dx), ady = Math.Abs(dy);
                int sx = dx < 0 ? -1 : 1, sy = dy < 0 ? -1 : 1;
                if (_tool == PaintTool.Line)
                {
                    if (adx > ady * 2) { dy = 0; }
                    else if (ady > adx * 2) { dx = 0; }
                    else
                    {
                        int d = Math.Max(adx, ady);
                        // 斜線的長度也不能超出畫布
                        d = Math.Min(d, Math.Min(sx > 0 ? cw - 1 - _shapeX0 : _shapeX0, sy > 0 ? ch - 1 - _shapeY0 : _shapeY0));
                        dx = sx * d; dy = sy * d;
                    }
                }
                else
                {
                    int d = Math.Max(adx, ady);
                    d = Math.Min(d, Math.Min(sx > 0 ? cw - 1 - _shapeX0 : _shapeX0, sy > 0 ? ch - 1 - _shapeY0 : _shapeY0));
                    dx = sx * d; dy = sy * d;
                }
                x = _shapeX0 + dx;
                y = _shapeY0 + dy;
            }

            if (x == _shapeX1 && y == _shapeY1) { return; }

            _shapeX1 = x;
            _shapeY1 = y;
            InvalidateVisual();
        }

        /// <summary>放開（commit = true）就把形狀畫下去、取消（false）就丟掉；回傳有沒有真的改到東西。</summary>
        internal bool ShapeEnd(bool commit)
        {
            if (!_shapeDragging) { return false; }

            _shapeDragging = false;
            if (IsMouseCaptured) { ReleaseMouseCapture(); }
            InvalidateVisual();
            if (!commit || _model == null) { return false; }
            // 拖曳途中換了層／視角／幀：座標對不上了，整筆取消
            if (!ReferenceEquals(_shapeModel, _model) || _shapeView != _view || _shapeSlice != _slice) { return false; }

            List<int[]> cells = ShapeCells(_tool, _shapeX0, _shapeY0, _shapeX1, _shapeY1, ShapeFilled);
            byte value = (_shapeButton == MouseButton.Right) ? (byte)0 : CurrentPaintIndex();
            BeginStroke(_shapeButton, value);
            _stampChanged = false;
            foreach (int[] c in cells) { Brush.ForEachVoxel(c[0], c[1], StampVoxel); }
            bool changed = _stampChanged;
            if (changed) { _strokeModel.NotifyChanged(); }
            EndStroke();
            return changed;
        }

        public void CancelShape() { ShapeEnd(false); }

        /// <summary>目前工具、兩個角（含頭尾）對應的畫布格子清單。</summary>
        internal static List<int[]> ShapeCells(PaintTool tool, int x0, int y0, int x1, int y1, bool filled)
        {
            switch (tool)
            {
                case PaintTool.Line: return LineCells(x0, y0, x1, y1);
                case PaintTool.Rectangle: return RectCells(x0, y0, x1, y1, filled);
                default: return EllipseCells(x0, y0, x1, y1, filled);
            }
        }

        /// <summary>Bresenham 直線（含兩端）。</summary>
        internal static List<int[]> LineCells(int x0, int y0, int x1, int y1)
        {
            // Bresenham 從哪一端出發結果會差一格；固定從「左邊（同一列就上面）」那端出發，這樣往哪個方向拖出來的線都一樣
            if (x0 > x1 || (x0 == x1 && y0 > y1)) { int tx = x0, ty = y0; x0 = x1; y0 = y1; x1 = tx; y1 = ty; }

            List<int[]> cells = new List<int[]>();
            int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;
            while (true)
            {
                cells.Add(new[] { x0, y0 });
                if (x0 == x1 && y0 == y1) { break; }

                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
            return cells;
        }

        /// <summary>矩形：filled = 整個填滿，否則只有最外圈一格寬的外框。兩個角的順序不拘。</summary>
        internal static List<int[]> RectCells(int x0, int y0, int x1, int y1, bool filled)
        {
            int ax = Math.Min(x0, x1), bx = Math.Max(x0, x1), ay = Math.Min(y0, y1), by = Math.Max(y0, y1);
            List<int[]> cells = new List<int[]>();
            for (int y = ay; y <= by; y++)
            {
                for (int x = ax; x <= bx; x++)
                {
                    if (filled || x == ax || x == bx || y == ay || y == by) { cells.Add(new[] { x, y }); }
                }
            }
            return cells;
        }

        /// <summary>
        /// 橢圓：外接矩形 (x0,y0)-(x1,y1)（含頭尾），格子中心落在橢圓內就算（跟球形同樣的取格方式：半軸 = 寬／高的一半）。
        /// filled = 實心，否則只留最外圈（內部格子且上下左右有一格在橢圓外）。寬或高只有 1 格時退化成一條線。
        /// </summary>
        internal static List<int[]> EllipseCells(int x0, int y0, int x1, int y1, bool filled)
        {
            int ax = Math.Min(x0, x1), bx = Math.Max(x0, x1), ay = Math.Min(y0, y1), by = Math.Max(y0, y1);
            int w = bx - ax + 1, h = by - ay + 1;
            double cx = (ax + bx) / 2.0, cy = (ay + by) / 2.0, rx = w / 2.0, ry = h / 2.0;
            bool[] inside = new bool[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double nx = (ax + x - cx) / rx, ny = (ay + y - cy) / ry;
                    inside[y * w + x] = nx * nx + ny * ny <= 1 + 1e-9;
                }
            }

            List<int[]> cells = new List<int[]>();
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (!inside[y * w + x]) { continue; }

                    bool edge = x == 0 || y == 0 || x == w - 1 || y == h - 1
                        || !inside[y * w + x - 1] || !inside[y * w + x + 1] || !inside[(y - 1) * w + x] || !inside[(y + 1) * w + x];
                    if (filled || edge) { cells.Add(new[] { ax + x, ay + y }); }
                }
            }
            return cells;
        }

        private static readonly Brush ShapePreviewBrush = MakeFrozenBrush(Color.FromArgb(120, 51, 153, 255));

        private static Brush MakeFrozenBrush(Color c) { SolidColorBrush b = new SolidColorBrush(c); b.Freeze(); return b; }

        /// <summary>拖曳中的形狀預覽（藍色半透明）：每個路徑格子畫出筆刷在目前這一層的剖面，鏡像開著時對稱位置也畫。</summary>
        private void DrawShapePreview(DrawingContext dc, Rect rect, int cw, int ch)
        {
            HashSet<int> set = new HashSet<int>();
            int axX, axY, axSight;
            CanvasAxes(out axX, out axY, out axSight);
            bool mirrorCx = MirrorOnAxis(axX), mirrorCy = MirrorOnAxis(axY);
            foreach (int[] c in ShapeCells(_tool, _shapeX0, _shapeY0, _shapeX1, _shapeY1, ShapeFilled))
            {
                Brush.ForEachCell(c[0], c[1], (x, y) =>
                {
                    for (int mask = 0; mask < 4; mask++)
                    {
                        if (((mask & 1) != 0 && !mirrorCx) || ((mask & 2) != 0 && !mirrorCy)) { continue; }

                        int mx = (mask & 1) != 0 ? cw - 1 - x : x, my = (mask & 2) != 0 ? ch - 1 - y : y;
                        if ((uint)mx < (uint)cw && (uint)my < (uint)ch) { set.Add(my * cw + mx); }
                    }
                });
            }
            foreach (int k in set)
            {
                dc.DrawRectangle(ShapePreviewBrush, null, new Rect(rect.X + (k % cw) * _scale, rect.Y + (k / cw) * _scale, _scale, _scale));
            }
        }

        private bool _picking;
        private int _lastPicked = -1;

        private void PickAt(int cx, int cy)
        {
            if (_model == null || (uint)cx >= (uint)CanvasWidth || (uint)cy >= (uint)CanvasHeight) { return; }

            int origin, stepX, stepY;
            ViewMapper.Plane(_view, _slice, _model.Width, _model.Height, _model.Depth, out origin, out stepX, out stepY);
            VoxelModel src = _model.ParentFrame ?? _model;   // 多圖層時吸合成後看到的顏色
            byte v = src.Data[origin + cx * stepX + cy * stepY];
            if (v == _lastPicked) { return; }

            _lastPicked = v;
            ColorPicked?.Invoke(v);
        }

        private bool _showBelow = true;
        public bool ShowBelow
        {
            get => _showBelow;
            set { _showBelow = value; Rebuild(); }
        }

        private bool _showPrevFrame = true;
        public bool ShowPrevFrame
        {
            get => _showPrevFrame;
            set { _showPrevFrame = value; Rebuild(); }
        }

        private bool _showGrid = true;
        public bool ShowGrid
        {
            get => _showGrid;
            set { _showGrid = value; InvalidateVisual(); }
        }

        public LayerCanvas()
        {
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
            SnapsToDevicePixels = true;
            ClipToBounds = true;
        }

        private static Pen MakePen(Color color)
        {
            Pen pen = new Pen(new SolidColorBrush(color), 1);
            pen.Freeze();
            return pen;
        }

        // --- 尺寸 / 視角 -----------------------------------------------------------------------------------
        public int CanvasWidth => _model == null ? 0 : ViewMapper.CanvasWidth(_view, _model.Width, _model.Height, _model.Depth);
        public int CanvasHeight => _model == null ? 0 : ViewMapper.CanvasHeight(_view, _model.Width, _model.Height, _model.Depth);

        /// <summary>目前視角有幾個切片（切片滑桿的範圍）。</summary>
        public int SliceCount => _model == null ? 0 : ViewMapper.SliceCount(_view, _model.Width, _model.Height, _model.Depth);

        public ViewDirection View
        {
            get => _view;
            set
            {
                if (_view == value) { return; }

                EndStroke();
                ResetSelectionState();   // 浮動內容先放下（用舊視角的對應），選取框是跟著畫布方向的，換視角就取消
                _view = value;
                _hoverX = _hoverY = int.MinValue;
                if (_model != null) { _slice = Math.Max(0, Math.Min(_slice, SliceCount - 1)); }
                Rebuild();
            }
        }

        public int Slice
        {
            get => _slice;
            set
            {
                if (_model == null) { return; }

                value = Math.Max(0, Math.Min(value, SliceCount - 1));
                if (value == _slice) { return; }

                _slice = value;
                Rebuild();
            }
        }

        /// <summary>目前編輯的那一幀。換幀時（尺寸相同）會重用同一張 bitmap。</summary>
        public VoxelModel Model
        {
            get => _model;
            set
            {
                if (ReferenceEquals(_model, value)) { return; }

                EndStroke();   // 換幀時如果還在畫，先收筆
                ResetSelectionState();   // 浮動內容先放進原本那一幀，選取框取消
                if (_model != null) { _model.Changed -= Rebuild; }

                _model = value;
                _hoverX = _hoverY = int.MinValue;

                if (_model != null)
                {
                    _model.Changed += Rebuild;
                    _slice = Math.Max(0, Math.Min(_slice, SliceCount - 1));
                }
                Rebuild();
            }
        }

        // --- 多圖層：背景參考與 3D 選取 ----------------------------------------------------------------------
        private VoxelModel _backdrop;

        /// <summary>
        /// 編輯的圖層「以外」的其他圖層合成的結果（null = 單層，沒有背景）。畫在目前圖層的透明處底下，半透明，方便對位；它被改動時會跟著重畫。
        /// </summary>
        public VoxelModel Backdrop
        {
            get => _backdrop;
            set
            {
                if (ReferenceEquals(_backdrop, value)) { return; }

                if (_backdrop != null) { _backdrop.Changed -= Rebuild; }
                _backdrop = value;
                if (_backdrop != null) { _backdrop.Changed += Rebuild; }
                Rebuild();
            }
        }

        private bool[] _voxelSelection;

        /// <summary>3D 魔法棒選出來的體素（索引跟 Model.Data 相同，長度要一樣；null = 沒有選取）。畫布上被選到的格子染成黃色。</summary>
        public bool[] VoxelSelection
        {
            get => _voxelSelection;
            set { _voxelSelection = value; Rebuild(); }
        }

        /// <summary>3D 魔法棒工具點了畫布：參數 = 點到的格子在 Model.Data 裡的索引、有沒有按 Shift（加選）、有沒有按 Ctrl（減選）。</summary>
        public event Action<int, bool, bool> WandClicked;

        /// <summary>目前這一片裡某個畫布格對應到 Model.Data 的索引（超出畫布回傳 -1）。</summary>
        public int IndexAtCell(int cx, int cy)
        {
            if (_model == null || cx < 0 || cy < 0 || cx >= CanvasWidth || cy >= CanvasHeight) { return -1; }

            int origin, stepX, stepY;
            ViewMapper.Plane(_view, _slice, _model.Width, _model.Height, _model.Depth, out origin, out stepX, out stepY);
            return origin + cx * stepX + cy * stepY;
        }

        /// <summary>當作參考的上一幀（null = 沒有）。它被修改時（例如復原）會跟著重畫。</summary>
        public VoxelModel PrevFrame
        {
            get => _prevFrame;
            set
            {
                if (ReferenceEquals(_prevFrame, value)) { return; }

                if (_prevFrame != null) { _prevFrame.Changed -= Rebuild; }
                _prevFrame = value;
                if (_prevFrame != null) { _prevFrame.Changed += Rebuild; }
                Rebuild();
            }
        }

        // --- 合成顯示用的 bitmap --------------------------------------------------------------------------
        private void EnsureBitmap()
        {
            if (_model == null) { _bmp = null; _buf = null; return; }

            int cw = CanvasWidth, ch = CanvasHeight;
            if (_bmp == null || _bmp.PixelWidth != cw || _bmp.PixelHeight != ch)
            {
                _bmp = new WriteableBitmap(cw, ch, 96, 96, PixelFormats.Bgra32, null);
                _buf = new int[cw * ch];
            }
        }

        private void Rebuild()
        {
            EnsureBitmap();
            if (_model != null && _bmp != null)
            {
                int w = _model.Width, h = _model.Height, d = _model.Depth;
                int cw = CanvasWidth, ch = CanvasHeight;
                byte[] data = _model.Data;
                int[] pal = _model.Palette;

                int origin, stepX, stepY;
                ViewMapper.Plane(_view, _slice, w, h, d, out origin, out stepX, out stepY);

                // 後面那一片（切片編號 -1）的半透明參考
                bool hasBelow = _showBelow && _slice > 0;
                int belowOrigin = 0, ignoreX, ignoreY;
                if (hasBelow) { ViewMapper.Plane(_view, _slice - 1, w, h, d, out belowOrigin, out ignoreX, out ignoreY); }

                byte[] backData = null;
                if (_backdrop != null && _backdrop.Data.Length == data.Length) { backData = _backdrop.Data; }
                bool[] voxSel = _voxelSelection != null && _voxelSelection.Length == data.Length ? _voxelSelection : null;

                byte[] prevData = null;
                if (_showPrevFrame && _prevFrame != null && !ReferenceEquals(_prevFrame, _model)
                    && _prevFrame.Width == w && _prevFrame.Height == h && _prevFrame.Depth == d)
                {
                    prevData = _prevFrame.Data;
                }

                // 選取工具把內容拖起來（還沒放下）：原位置暫時顯示成空的
                int hx0, hy0, hx1, hy1;
                bool[] hideMask;
                bool hide = TryGetHiddenRegion(out hx0, out hy0, out hx1, out hy1, out hideMask);

                for (int cy = 0; cy < ch; cy++)
                {
                    for (int cx = 0; cx < cw; cx++)
                    {
                        int idx = origin + cx * stepX + cy * stepY;
                        int bg = ((cx + cy) & 1) == 0 ? CheckA : CheckB;
                        byte v = data[idx];
                        int c;

                        if (hide && cx >= hx0 && cx < hx1 && cy >= hy0 && cy < hy1
                            && (hideMask == null || hideMask[(cy - hy0) * (hx1 - hx0) + (cx - hx0)])) { c = bg; }
                        else if (v != 0)
                        {
                            // 半透明的顏色疊在棋盤格上，看得出它是半透明
                            c = pal[v];
                            int a = (int)((uint)c >> 24);
                            if (a < 255) { c = Blend(bg, c, a + (a >> 7)); }

                            if (voxSel != null && voxSel[idx]) { c = Blend(c, SelectTint, 140); }
                        }
                        else if (backData != null && backData[idx] != 0)
                        {
                            // 其他圖層的內容：半透明參考（只在目前圖層是透明的地方露出來）
                            c = Blend(bg, pal[backData[idx]], BackdropAlpha);
                        }
                        else if (prevData != null && prevData[idx] != 0)
                        {
                            // 上一幀的同一片：染成橘色調，跟「後面那一片」的參考區分開
                            c = Blend(bg, Blend(pal[prevData[idx]], PrevFrameTint, 170), GhostAlpha + 40);
                        }
                        else if (hasBelow && data[belowOrigin + cx * stepX + cy * stepY] != 0)
                        {
                            c = Blend(bg, pal[data[belowOrigin + cx * stepX + cy * stepY]], GhostAlpha);
                        }
                        else { c = bg; }

                        _buf[cy * cw + cx] = c;
                    }
                }
                _bmp.WritePixels(new Int32Rect(0, 0, cw, ch), _buf, cw * 4, 0);
            }
            InvalidateVisual();
        }

        private static int Blend(int bg, int fg, int alpha)
        {
            int inv = 256 - alpha;
            int r = ((((bg >> 16) & 255) * inv) + (((fg >> 16) & 255) * alpha)) >> 8;
            int g = ((((bg >> 8) & 255) * inv) + (((fg >> 8) & 255) * alpha)) >> 8;
            int b = (((bg & 255) * inv) + ((fg & 255) * alpha)) >> 8;
            return unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
        }

        // --- 倍率與平移 ------------------------------------------------------------------------------------
        /// <summary>可選的倍率（每個體素在畫面上佔幾像素）；0 = 自動適合視窗。</summary>
        public static readonly int[] ZoomSteps = { 1, 2, 3, 4, 6, 8, 10, 12, 16, 20, 24, 32, 48, 64 };

        private int _zoom;   // 0 = 自動適合視窗（跟以前一樣撐到最大）；其他 = 固定倍率
        private double _panX, _panY;   // 畫布內容中心相對於視窗中心的位移（像素）；放大到超出視窗時才有意義
        private bool _panning;
        private Point _panStart;
        private double _panX0, _panY0;

        /// <summary>倍率改變時觸發（介面上的倍率選單用來同步）。</summary>
        public event Action ZoomChanged;

        /// <summary>編輯畫布的倍率：0 = 自動適合視窗，其他 = 固定倍率（1~64）。</summary>
        public int Zoom
        {
            get => _zoom;
            set
            {
                value = Math.Max(0, Math.Min(ZoomSteps[ZoomSteps.Length - 1], value));
                if (_zoom == value) { return; }

                if (_model != null && ActualWidth > 0) { ZoomAt(value, new Point(ActualWidth / 2, ActualHeight / 2)); }
                else { _zoom = value; _panX = _panY = 0; ZoomChanged?.Invoke(); InvalidateVisual(); }
            }
        }

        /// <summary>改成新倍率，並讓 anchor（畫布控制項座標）底下的那一格維持在原地不動。</summary>
        public void ZoomAt(int newZoom, Point anchor)
        {
            if (_model == null || CanvasWidth == 0) { return; }

            UpdateMetrics();
            double fx = (anchor.X - _origin.X) / _scale, fy = (anchor.Y - _origin.Y) / _scale;
            _zoom = newZoom;
            _panX = _panY = 0;
            UpdateMetrics();
            if (_zoom != 0)
            {
                double ox = anchor.X - fx * _scale, oy = anchor.Y - fy * _scale;
                _panX = ox + CanvasWidth * _scale / 2 - ActualWidth / 2;
                _panY = oy + CanvasHeight * _scale / 2 - ActualHeight / 2;
                UpdateMetrics();
            }
            ZoomChanged?.Invoke();
            InvalidateVisual();
        }

        /// <summary>往上一檔／下一檔倍率（direction &gt; 0 放大、&lt; 0 縮小）。縮小到不比「適合視窗」小時就回到自動適合。</summary>
        public void ZoomStep(int direction, Point anchor)
        {
            if (_model == null || CanvasWidth == 0) { return; }

            UpdateMetrics();
            int cur = (int)_scale;
            int next = cur;
            if (direction > 0) { foreach (int s in ZoomSteps) { if (s > cur) { next = s; break; } } }
            else { for (int i = ZoomSteps.Length - 1; i >= 0; i--) { if (ZoomSteps[i] < cur) { next = ZoomSteps[i]; break; } } }

            int fit = FitScale();
            if (next <= fit) { next = 0; }
            if (next != _zoom) { ZoomAt(next, anchor); }
        }

        private int FitScale()
        {
            double s = Math.Floor(Math.Min(ActualWidth / CanvasWidth, ActualHeight / CanvasHeight));
            return s < 1 ? 1 : (int)s;
        }

        // --- 捲軸資訊（讓外面的水平／垂直捲軸跟畫布同步）-----------------------------------------------------
        /// <summary>捲動位置、可捲範圍或視窗大小有變時觸發（已延後到繪製完成之後，可以安全地改介面）。</summary>
        public event Action ScrollInfoChanged;

        private double _repMaxX = -1, _repMaxY = -1, _repViewW = -1, _repViewH = -1, _repPosX = -1, _repPosY = -1;
        private bool _scrollPosted;

        /// <summary>目前的捲動狀態：可捲範圍（像素，0 = 沒超出視窗）、視窗大小、目前位置（0..範圍）。</summary>
        public void GetScrollInfo(out double maxX, out double maxY, out double viewW, out double viewH, out double posX, out double posY)
        {
            viewW = ActualWidth;
            viewH = ActualHeight;
            maxX = maxY = posX = posY = 0;
            if (_model == null || CanvasWidth == 0) { return; }

            UpdateMetrics();
            double contentW = CanvasWidth * _scale, contentH = CanvasHeight * _scale;
            maxX = Math.Max(0, contentW - viewW);
            maxY = Math.Max(0, contentH - viewH);
            posX = maxX > 0 ? Math.Max(0, Math.Min(maxX, maxX / 2 - _panX)) : 0;
            posY = maxY > 0 ? Math.Max(0, Math.Min(maxY, maxY / 2 - _panY)) : 0;
        }

        /// <summary>捲到指定位置（像素，0 = 最左／最上）；沒超出視窗的那一軸不動。</summary>
        public void ScrollTo(double posX, double posY)
        {
            if (_model == null || CanvasWidth == 0) { return; }

            double maxX, maxY, vw, vh, cx, cy;
            GetScrollInfo(out maxX, out maxY, out vw, out vh, out cx, out cy);
            if (maxX > 0) { _panX = maxX / 2 - Math.Max(0, Math.Min(maxX, posX)); }
            if (maxY > 0) { _panY = maxY / 2 - Math.Max(0, Math.Min(maxY, posY)); }
            UpdateMetrics();
            InvalidateVisual();
        }

        /// <summary>往右／往下捲 dx、dy 像素（負數 = 往左／往上）。</summary>
        public void ScrollBy(double dx, double dy)
        {
            double maxX, maxY, vw, vh, px, py;
            GetScrollInfo(out maxX, out maxY, out vw, out vh, out px, out py);
            ScrollTo(px + dx, py + dy);
        }

        private void NotifyScrollIfChanged()
        {
            double maxX, maxY, vw, vh, px, py;
            GetScrollInfo(out maxX, out maxY, out vw, out vh, out px, out py);
            if (maxX == _repMaxX && maxY == _repMaxY && vw == _repViewW && vh == _repViewH && px == _repPosX && py == _repPosY) { return; }
            if (_scrollPosted) { return; }

            _scrollPosted = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _scrollPosted = false;
                double a, b, c, d, e, f;
                GetScrollInfo(out a, out b, out c, out d, out e, out f);
                _repMaxX = a; _repMaxY = b; _repViewW = c; _repViewH = d; _repPosX = e; _repPosY = f;
                ScrollInfoChanged?.Invoke();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        // --- 版面 / 座標 ----------------------------------------------------------------------------------
        private void UpdateMetrics()
        {
            double s = _zoom > 0 ? _zoom : FitScale();
            _scale = s;

            double contentW = CanvasWidth * s, contentH = CanvasHeight * s;
            double maxX = Math.Max(0, (contentW - ActualWidth) / 2), maxY = Math.Max(0, (contentH - ActualHeight) / 2);
            if (_zoom == 0) { _panX = _panY = 0; }
            _panX = Math.Max(-maxX, Math.Min(maxX, _panX));   // 內容比視窗小的那一軸固定置中；超出的那一軸不能拖到露出空白太多
            _panY = Math.Max(-maxY, Math.Min(maxY, _panY));
            _origin = new Point(
                Math.Floor((ActualWidth - contentW) / 2 + _panX),
                Math.Floor((ActualHeight - contentH) / 2 + _panY));
        }

        private void CellAt(Point p, out int cx, out int cy)
        {
            cx = (int)Math.Floor((p.X - _origin.X) / _scale);
            cy = (int)Math.Floor((p.Y - _origin.Y) / _scale);
        }

        protected override void OnRender(DrawingContext dc)
        {
            // 全區塗一層透明底，滑鼠事件才收得到
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
            if (_model == null || _bmp == null) { return; }

            UpdateMetrics();
            int cw = CanvasWidth, ch = CanvasHeight;
            Rect rect = new Rect(_origin.X, _origin.Y, cw * _scale, ch * _scale);
            dc.DrawImage(_bmp, rect);

            if (_showGrid && _scale >= 8)
            {
                for (int x = 0; x <= cw; x++)
                {
                    double px = rect.X + x * _scale;
                    dc.DrawLine(GridPen, new Point(px, rect.Y), new Point(px, rect.Bottom));
                }
                for (int y = 0; y <= ch; y++)
                {
                    double py = rect.Y + y * _scale;
                    dc.DrawLine(GridPen, new Point(rect.X, py), new Point(rect.Right, py));
                }
            }

            // 鏡像軸線：不管游標在不在畫布上都顯示（只有畫筆／橡皮擦／填滿會用到鏡像，選取、吸管不畫）
            int axX, axY, axSight;
            CanvasAxes(out axX, out axY, out axSight);
            bool mirrorCx = MirrorOnAxis(axX), mirrorCy = MirrorOnAxis(axY);
            bool mirrorTool = Tool != PaintTool.Picker;   // 吸管不吃鏡像
            if (!IsSelectionTool && mirrorTool) { DrawMirrorGuides(dc, rect, mirrorCx, mirrorCy); }

            if (_hoverX != int.MinValue && !IsSelectionTool)
            {
                // 填滿、吸管工具只框出游標所在的那一格（不吃筆刷粗細）
                Action<Action<int, int>> eachCell = act =>
                {
                    if (Tool == PaintTool.Fill || Tool == PaintTool.Picker) { act(_hoverX, _hoverY); }
                    else { Brush.ForEachCell(_hoverX, _hoverY, act); }
                };
                bool drawMirrors = mirrorTool && (mirrorCx || mirrorCy);
                eachCell((cx, cy) =>
                {
                    if ((uint)cx < (uint)cw && (uint)cy < (uint)ch)
                    {
                        Rect cell = new Rect(rect.X + cx * _scale, rect.Y + cy * _scale, _scale, _scale);
                        dc.DrawRectangle(null, HoverPenOuter, cell);
                        if (_scale >= 5) { cell.Inflate(-1, -1); dc.DrawRectangle(null, HoverPen, cell); }

                        // 鏡像：在這個視角看得到的對稱位置也框出來（粉紅），畫下去那些格子會同時被改
                        if (drawMirrors)
                        {
                            for (int mask = 1; mask < 4; mask++)
                            {
                                if (((mask & 1) != 0 && !mirrorCx) || ((mask & 2) != 0 && !mirrorCy)) { continue; }

                                int mx = (mask & 1) != 0 ? cw - 1 - cx : cx, my = (mask & 2) != 0 ? ch - 1 - cy : cy;
                                if ((mx == cx && my == cy) || (uint)mx >= (uint)cw || (uint)my >= (uint)ch) { continue; }

                                dc.DrawRectangle(null, MirrorPen, new Rect(rect.X + mx * _scale, rect.Y + my * _scale, _scale, _scale));
                            }
                        }
                    }
                });
            }

            if (_shapeDragging) { DrawShapePreview(dc, rect, cw, ch); }

            DrawSelectionOverlay(dc, rect);   // 選取框（螞蟻線）與浮動內容
            NotifyScrollIfChanged();
        }

        private static readonly Pen MirrorPen = MakePen(Color.FromArgb(230, 255, 64, 192));
        private static readonly Pen MirrorGuidePen = MakeDashedPen(Color.FromArgb(200, 255, 64, 192));

        private static Pen MakeDashedPen(Color color)
        {
            Pen pen = new Pen(new SolidColorBrush(color), 1) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
            pen.Freeze();
            return pen;
        }

        /// <summary>對稱軸線：落在畫布平面上的鏡像軸，在畫布正中央畫一條粉紅虛線（沿視線方向的軸沒有線，因為它是「切片」的正中間）。</summary>
        private void DrawMirrorGuides(DrawingContext dc, Rect rect, bool vertical, bool horizontal)
        {
            if (vertical) { dc.DrawLine(MirrorGuidePen, new Point(rect.X + rect.Width / 2, rect.Y), new Point(rect.X + rect.Width / 2, rect.Bottom)); }
            if (horizontal) { dc.DrawLine(MirrorGuidePen, new Point(rect.X, rect.Y + rect.Height / 2), new Point(rect.Right, rect.Y + rect.Height / 2)); }
        }

        // --- 滑鼠 -----------------------------------------------------------------------------------------
        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            if (_model == null || _painting) { return; }
            if (e.ChangedButton == MouseButton.Middle)
            {
                // 中鍵拖曳：平移畫布（放大到超出視窗時用）
                _panning = true;
                _panStart = e.GetPosition(this);
                _panX0 = _panX;
                _panY0 = _panY;
                CaptureMouse();
                e.Handled = true;
                return;
            }
            if (e.ChangedButton != MouseButton.Left && e.ChangedButton != MouseButton.Right) { return; }

            UpdateMetrics();
            CellAt(e.GetPosition(this), out int cx, out int cy);

            if (IsSelectionTool)
            {
                if (e.ChangedButton == MouseButton.Left)
                {
                    SelectMouseDown(cx, cy);
                    if (_selDragging || _floatDragging) { CaptureMouse(); }   // 魔術棒點一下就選好了，不需要抓住滑鼠
                }
                e.Handled = true;
                return;
            }

            if (Tool == PaintTool.Wand3D)
            {
                // 3D 魔法棒：左鍵點一格，由外面（主視窗）去算整個立體相連的範圍；Shift = 加選、Ctrl = 減選
                if (e.ChangedButton == MouseButton.Left)
                {
                    int index = IndexAtCell(cx, cy);
                    if (index >= 0) { WandClicked?.Invoke(index, (Keyboard.Modifiers & ModifierKeys.Shift) != 0, (Keyboard.Modifiers & ModifierKeys.Control) != 0); }
                }
                e.Handled = true;
                return;
            }

            if (Tool == PaintTool.Picker)
            {
                // 吸管：左鍵按下就取色，按著拖曳會一直取（鬆開停止）；右鍵不做事
                if (e.ChangedButton == MouseButton.Left)
                {
                    _picking = true;
                    _lastPicked = -1;
                    CaptureMouse();
                    PickAt(cx, cy);
                }
                e.Handled = true;
                return;
            }

            if (IsShapeTool)
            {
                // 直線／矩形／橢圓：左右鍵都可以（左 = 畫、右 = 清除）；按著拖曳預覽、放開才畫
                if (e.ChangedButton == MouseButton.Left || e.ChangedButton == MouseButton.Right)
                {
                    ShapeBegin(cx, cy, e.ChangedButton);
                    CaptureMouse();
                }
                e.Handled = true;
                return;
            }

            if (Tool == PaintTool.Fill)
            {
                // 填滿是一次性動作：左鍵填色、右鍵把該區清成透明
                FillAt(cx, cy, e.ChangedButton == MouseButton.Right ? (byte)0 : CurrentPaintIndex());
                e.Handled = true;
                return;
            }

            BeginStroke(e.ChangedButton, (e.ChangedButton == MouseButton.Right || Tool == PaintTool.Eraser) ? (byte)0 : CurrentPaintIndex());
            CaptureMouse();

            StampLine(cx, cy, cx, cy);
            _lastX = cx;
            _lastY = cy;
            e.Handled = true;
        }

        /// <summary>鎖定起筆當下的幀 / 視角 / 切片對應，並開始記錄被改格子的原值。</summary>
        private void BeginStroke(MouseButton button, byte value)
        {
            _strokeButton = button;
            _strokeValue = value;
            _strokeModel = _model;
            _strokeView = _view;
            _strokeSlice = _slice;
            ViewMapper.Plane(_view, _slice, _model.Width, _model.Height, _model.Depth,
                out _strokePlaneOrigin, out _strokeStepX, out _strokeStepY);

            // 筆刷在視線方向涵蓋的每一片（方形／圓形 = 目前切片 + 往後面的 depth-1 片；立方體／膠囊 = 以目前切片為中心前後各延伸），
            // 超出模型（第 0 片之前、最後一片之後）的片不畫。每片各自的平面起點（步進相同）
            int dzMin, dzMax;
            Brush.DzRange(out dzMin, out dzMax);
            int sliceCount = SliceCount;
            _strokeDzMin = dzMin;
            _strokeOrigins = new int[dzMax - dzMin + 1];
            for (int i = 0; i < _strokeOrigins.Length; i++)
            {
                int s = _slice + dzMin + i;
                if (s < 0 || s >= sliceCount) { _strokeOrigins[i] = int.MinValue; continue; }

                int o, sx, sy;
                ViewMapper.Plane(_view, s, _model.Width, _model.Height, _model.Depth, out o, out sx, out sy);
                _strokeOrigins[i] = o;
            }
            _strokeCw = CanvasWidth;
            _strokeCh = CanvasHeight;
            _strokeMx = MirrorX; _strokeMy = MirrorY; _strokeMz = MirrorZ;
            _strokeOld = new Dictionary<int, byte>();
            _painting = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_model == null) { return; }

            if (_panning)
            {
                Point p = e.GetPosition(this);
                _panX = _panX0 + (p.X - _panStart.X);
                _panY = _panY0 + (p.Y - _panStart.Y);
                UpdateMetrics();
                InvalidateVisual();
                return;
            }

            UpdateMetrics();
            CellAt(e.GetPosition(this), out int cx, out int cy);
            _hoverX = cx;
            _hoverY = cy;
            ReportHover(cx, cy);

            if (_painting)
            {
                StampLine(_lastX, _lastY, cx, cy);
                _lastX = cx;
                _lastY = cy;
            }
            else if (_shapeDragging) { ShapeMove(cx, cy, (Keyboard.Modifiers & ModifierKeys.Shift) != 0); }
            else if (_picking) { PickAt(cx, cy); }
            else if (_selDragging || _floatDragging) { SelectMouseMove(cx, cy); }
            InvalidateVisual();
        }


        private void ReportHover(int cx, int cy)
        {
            if (HoverVoxel == null) { return; }

            if ((uint)cx < (uint)CanvasWidth && (uint)cy < (uint)CanvasHeight)
            {
                int origin, stepX, stepY, x, y, z;
                ViewMapper.Plane(_view, _slice, _model.Width, _model.Height, _model.Depth, out origin, out stepX, out stepY);
                ViewMapper.ToVoxel(origin + cx * stepX + cy * stepY, _model.Width, _model.Height, out x, out y, out z);
                HoverVoxel(x, y, z);
            }
            else { HoverVoxel(-1, -1, -1); }
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            base.OnMouseUp(e);
            if (_panning && e.ChangedButton == MouseButton.Middle)
            {
                _panning = false;
                if (IsMouseCaptured) { ReleaseMouseCapture(); }
                return;
            }
            if (_shapeDragging && e.ChangedButton == _shapeButton)
            {
                ShapeMove(_hoverX, _hoverY, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);   // 放開那一刻游標的位置
                ShapeEnd(true);
            }
            else if (_picking && e.ChangedButton == MouseButton.Left)
            {
                _picking = false;
                if (IsMouseCaptured) { ReleaseMouseCapture(); }
            }
            else if (_painting && e.ChangedButton == _strokeButton) { EndStroke(); }
            else if (e.ChangedButton == MouseButton.Left && (_selDragging || _floatDragging))
            {
                EndSelectDrag();
                if (IsMouseCaptured) { ReleaseMouseCapture(); }
            }
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            _panning = false;
            _picking = false;
            if (_shapeDragging) { ShapeEnd(false); }   // 被打斷（例如切換視窗）：這一筆取消
            EndStroke();
            EndSelectDrag();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            if (!_painting)
            {
                _hoverX = _hoverY = int.MinValue;
                HoverVoxel?.Invoke(-1, -1, -1);
                InvalidateVisual();
            }
        }

        private void EndStroke()
        {
            if (!_painting) { return; }
            _painting = false;
            if (IsMouseCaptured) { ReleaseMouseCapture(); }

            VoxelModel model = _strokeModel;
            Dictionary<int, byte> old = _strokeOld;
            _strokeModel = null;
            _strokeOld = null;

            if (old == null || old.Count == 0) { return; }

            int[] indices = new int[old.Count];
            byte[] values = new byte[old.Count];
            int n = 0;
            foreach (KeyValuePair<int, byte> kv in old)
            {
                indices[n] = kv.Key;
                values[n] = kv.Value;
                n++;
            }
            StrokeEnded?.Invoke(model, _strokeView, _strokeSlice, indices, values);
        }

        // --- 填滿 -----------------------------------------------------------------------------------------
        /// <summary>
        /// 在畫布格 (cx, cy) 填滿：把跟該格同色且連通（上下左右四向，或 <see cref="Fill3D"/> 時六向）的格子都換成 value。
        /// 平面填滿（不是立體填滿）會吃筆刷深度：以「目前這一層」連通的區域當範本，同樣的格子也填到往後面（切片編號變小）的
        /// 深度−1 層（那幾層本來是什麼顏色都會被蓋掉，跟畫筆的深度一樣）。立體填滿本來就跨層連通，所以不使用深度。
        /// 在畫布外、立體填滿卻點在空白格、或完全沒有東西會改變時什麼都不做。整個填滿算一次復原。
        /// </summary>
        private void FillAt(int cx, int cy, byte value)
        {
            if (_model == null) { return; }

            int cw = CanvasWidth, ch = CanvasHeight;
            if ((uint)cx >= (uint)cw || (uint)cy >= (uint)ch) { return; }

            VoxelModel m = _model;
            byte[] data = m.Data;
            int origin, stepX, stepY;
            ViewMapper.Plane(_view, _slice, m.Width, m.Height, m.Depth, out origin, out stepX, out stepY);

            int start = origin + cx * stepX + cy * stepY;
            byte target = data[start];
            int depth = Fill3D ? 1 : Math.Max(1, Math.Min(Brush.Depth, _slice + 1));
            if (target == value && depth == 1) { return; }   // 深度 ≥ 2 時，目前層雖然不用改，後面的層可能要
            if (Fill3D && target == 0) { return; }

            int[] indices;
            byte[] old;

            if (Fill3D)
            {
                List<int> changed = Flood3D(data, m.Width, m.Height, m.Depth, start, target, value);
                if (changed.Count == 0) { return; }

                indices = changed.ToArray();
                old = new byte[indices.Length];
                for (int i = 0; i < old.Length; i++) { old[i] = target; }   // 填的全是同色格，原值都一樣
            }
            else
            {
                // 範本：目前切片裡連通的同色區域（只找、不改）
                List<int> region = Flood2DRegion(data, origin, stepX, stepY, cw, ch, cx, cy, target);

                // 每一片的平面起點（[0] = 目前切片，之後依序往後面）；各片的步進相同
                int[] origins = new int[depth];
                for (int k = 0; k < depth; k++)
                {
                    int o, sx, sy;
                    ViewMapper.Plane(_view, _slice - k, m.Width, m.Height, m.Depth, out o, out sx, out sy);
                    origins[k] = o;
                }

                List<int> idxList = new List<int>();
                List<byte> oldList = new List<byte>();
                foreach (int idx0 in region)
                {
                    int offset = idx0 - origin;
                    for (int k = 0; k < depth; k++)
                    {
                        int idx = origins[k] + offset;
                        byte was = data[idx];
                        if (was == value) { continue; }

                        data[idx] = value;
                        idxList.Add(idx);
                        oldList.Add(was);   // 後面的層原本可能是別的顏色，要記真正的原值
                    }
                }
                if (idxList.Count == 0) { return; }

                indices = idxList.ToArray();
                old = oldList.ToArray();
            }

            if (MirrorAny) { ApplyMirrorToFill(m, value, ref indices, ref old); }

            m.NotifyChanged();
            StrokeEnded?.Invoke(m, _view, _slice, indices, old);
        }

        /// <summary>填滿改到的每一格，對稱位置也改成同樣的值（那裡原本是什麼顏色都會被蓋掉，跟筆畫的鏡像一樣）；原值照樣記下來給復原用。</summary>
        private void ApplyMirrorToFill(VoxelModel m, byte value, ref int[] indices, ref byte[] old)
        {
            Dictionary<int, byte> oldMap = new Dictionary<int, byte>(indices.Length * 2);
            for (int i = 0; i < indices.Length; i++) { oldMap[indices[i]] = old[i]; }

            byte[] data = m.Data;
            int[] buf = new int[7];
            for (int i = 0; i < indices.Length; i++)
            {
                int n = MirrorIndices(indices[i], m.Width, m.Height, m.Depth, MirrorX, MirrorY, MirrorZ, buf);
                for (int k = 0; k < n; k++)
                {
                    int j = buf[k];
                    if (oldMap.ContainsKey(j) || data[j] == value) { continue; }

                    oldMap[j] = data[j];
                    data[j] = value;
                }
            }

            indices = new int[oldMap.Count];
            old = new byte[oldMap.Count];
            int p = 0;
            foreach (KeyValuePair<int, byte> kv in oldMap) { indices[p] = kv.Key; old[p] = kv.Value; p++; }
        }

        /// <summary>找出從 (cx,cy) 出發、上下左右四向連通、值都等於 target 的畫布格（回傳模型線性索引；不修改資料）。</summary>
        private static List<int> Flood2DRegion(byte[] data, int origin, int stepX, int stepY, int cw, int ch,
            int cx, int cy, byte target)
        {
            List<int> region = new List<int>();
            bool[] seen = new bool[cw * ch];
            Stack<int> stack = new Stack<int>();
            stack.Push(cy * cw + cx);
            seen[cy * cw + cx] = true;

            while (stack.Count > 0)
            {
                int c = stack.Pop();
                int x = c % cw, y = c / cw;
                int idx = origin + x * stepX + y * stepY;
                if (data[idx] != target) { continue; }

                region.Add(idx);

                if (x > 0 && !seen[c - 1]) { seen[c - 1] = true; stack.Push(c - 1); }
                if (x < cw - 1 && !seen[c + 1]) { seen[c + 1] = true; stack.Push(c + 1); }
                if (y > 0 && !seen[c - cw]) { seen[c - cw] = true; stack.Push(c - cw); }
                if (y < ch - 1 && !seen[c + cw]) { seen[c + cw] = true; stack.Push(c + cw); }
            }
            return region;
        }
        private static List<int> Flood3D(byte[] data, int w, int h, int d, int start, byte target, byte value)
        {
            List<int> changed = new List<int>();
            int wh = w * h;
            bool[] seen = new bool[data.Length];
            Stack<int> stack = new Stack<int>();
            stack.Push(start);
            seen[start] = true;

            while (stack.Count > 0)
            {
                int i = stack.Pop();
                if (data[i] != target) { continue; }

                data[i] = value;
                changed.Add(i);

                int z = i / wh, rem = i - z * wh, y = rem / w, x = rem - y * w;
                if (x > 0 && !seen[i - 1]) { seen[i - 1] = true; stack.Push(i - 1); }
                if (x < w - 1 && !seen[i + 1]) { seen[i + 1] = true; stack.Push(i + 1); }
                if (y > 0 && !seen[i - w]) { seen[i - w] = true; stack.Push(i - w); }
                if (y < h - 1 && !seen[i + w]) { seen[i + w] = true; stack.Push(i + w); }
                if (z > 0 && !seen[i - wh]) { seen[i - wh] = true; stack.Push(i - wh); }
                if (z < d - 1 && !seen[i + wh]) { seen[i + wh] = true; stack.Push(i + wh); }
            }
            return changed;
        }

        /// <summary>從 (x0,y0) 到 (x1,y1) 用 Bresenham 連線，每一格蓋一次筆刷，快速拖曳也不會斷線。</summary>
        private void StampLine(int x0, int y0, int x1, int y1)
        {
            _stampChanged = false;

            int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;

            while (true)
            {
                Brush.ForEachVoxel(x0, y0, StampVoxel);
                if (x0 == x1 && y0 == y1) { break; }

                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }

            if (_stampChanged) { _strokeModel.NotifyChanged(); }   // 觸發 Rebuild + 預覽 / 縮圖重繪
        }

        /// <summary>畫一個體素：畫布格 (cx, cy) 在視線方向偏移 dz 的那一片；超出畫布或模型範圍就略過。</summary>
        private void StampVoxel(int cx, int cy, int dz)
        {
            if ((uint)cx >= (uint)_strokeCw || (uint)cy >= (uint)_strokeCh) { return; }

            int k = dz - _strokeDzMin;
            if ((uint)k >= (uint)_strokeOrigins.Length) { return; }

            int origin = _strokeOrigins[k];
            if (origin == int.MinValue) { return; }

            int index = origin + cx * _strokeStepX + cy * _strokeStepY;
            PutStrokeIndex(index);

            if (_strokeMx || _strokeMy || _strokeMz)   // 鏡像：對稱位置也蓋同樣的值
            {
                VoxelModel m = _strokeModel;
                int n = MirrorIndices(index, m.Width, m.Height, m.Depth, _strokeMx, _strokeMy, _strokeMz, _mirrorBuf);
                for (int i = 0; i < n; i++) { PutStrokeIndex(_mirrorBuf[i]); }
            }
        }

        private void PutStrokeIndex(int index)
        {
            byte old;
            if (!_strokeModel.SetIndex(index, _strokeValue, out old)) { return; }

            if (!_strokeOld.ContainsKey(index)) { _strokeOld[index] = old; }   // 只記第一次的原值
            _stampChanged = true;
        }
    }
}
