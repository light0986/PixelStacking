using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PixelStacking.Controls
{
    /// <summary>
    /// RGBA 調色盤：左邊一個方框（X = R、Y = G，往上 G 越大），右邊兩條直條（第一條 = B、第二條 = A，往上越大），
    /// 下面四個數值框 R / G / B / A（0~255）加一個目前顏色的色塊。點／拖方框或直條即可取色。
    /// Alpha 最小是 1（0 = 完全看不見的體素沒有意義，要清掉請用橡皮擦）。
    /// 上面有 RGB / HSV 兩個分頁：HSV 頁的方框 X = 飽和度、Y = 明度，第一條 = 色相、第二條 = A，數值框 H(0~360) S(0~100) V(0~100) A；
    /// 兩頁編輯的是同一個顏色，切換分頁時互相同步。
    /// </summary>
    public sealed class ColorPicker : StackPanel
    {
        // 顏色的「正本」是 RGB 這一頁的 _surface（R、G、B、A）。HSV 頁另外記 H、S、V 三個數（_hsv）：
        // 純灰、純黑時 RGB 反推不出色相／飽和度，所以 HSV 頁自己保留，切換分頁或拖曳時色相才不會亂跳。
        private readonly Surface _surface = new Surface();
        private readonly HsvSurface _hsv = new HsvSurface();
        private readonly TextBox _rBox = MakeBox("RGB_R"), _gBox = MakeBox("RGB_G"), _bBox = MakeBox("RGB_B"), _aBox = MakeBox("RGB_A");
        private readonly TextBox _hBox = MakeBox("HSV_H"), _sBox = MakeBox("HSV_S"), _vBox = MakeBox("HSV_V"), _haBox = MakeBox("HSV_A");
        private readonly StackPanel _rgbPage = new StackPanel(), _hsvPage = new StackPanel();
        private readonly RadioButton _tabRgb = MakeTab("RGB", "ColorTabRgb"), _tabHsv = MakeTab("HSV", "ColorTabHsv");
        private readonly Border _preview = new Border { Height = 18, Margin = new Thickness(0, 6, 0, 0), BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)), BorderThickness = new Thickness(1) };
        private readonly Border _previewColor = new Border();
        private bool _syncing;

        /// <summary>使用者用滑鼠或輸入框改了顏色時觸發（程式端設 <see cref="Argb"/> 不會觸發）。</summary>
        public event Action ColorChanged;

        /// <summary>使用者切換 RGB / HSV 分頁時觸發（程式端設 <see cref="HsvMode"/> 不會觸發）。</summary>
        public event Action ModeChanged;

        public ColorPicker()
        {
            // 分頁列：RGB | HSV
            Grid tabs = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            tabs.ColumnDefinitions.Add(new ColumnDefinition());
            tabs.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(_tabHsv, 1);
            tabs.Children.Add(_tabRgb);
            tabs.Children.Add(_tabHsv);
            _tabRgb.IsChecked = true;
            _tabRgb.Checked += Tab_Checked;
            _tabHsv.Checked += Tab_Checked;
            Children.Add(tabs);

            // RGB 頁
            _rgbPage.Children.Add(_surface);
            _rgbPage.Children.Add(MakeLabels("R", "G", "B", "A"));
            _rgbPage.Children.Add(MakeRow(_rBox, _gBox, _bBox, _aBox));
            Children.Add(_rgbPage);

            // HSV 頁：方框 X = 飽和度、Y = 明度；第一條 = 色相、第二條 = A
            _hsvPage.Children.Add(_hsv);
            _hsvPage.Children.Add(MakeLabels("H°", "S%", "V%", "A"));
            _hsvPage.Children.Add(MakeRow(_hBox, _sBox, _vBox, _haBox));
            _hsvPage.Visibility = Visibility.Collapsed;
            Children.Add(_hsvPage);

            _preview.Background = Surface.CheckerBrush(5);
            LightCheckerChanged += OnLightCheckerChanged;
            _preview.Child = _previewColor;
            Children.Add(_preview);

            _surface.Edited += Surface_Edited;
            _hsv.Edited += Hsv_Edited;
            WireBox(_rBox, CommitBoxes); WireBox(_gBox, CommitBoxes); WireBox(_bBox, CommitBoxes); WireBox(_aBox, CommitBoxes);
            WireBox(_hBox, CommitHsvBoxes); WireBox(_sBox, CommitHsvBoxes); WireBox(_vBox, CommitHsvBoxes); WireBox(_haBox, CommitHsvBoxes);
            SyncHsvFromRgb();
            SyncBoxes();
        }

        /// <summary>目前顏色（ARGB，A 在最高位元組）。</summary>
        public int Argb
        {
            get { return _surface.Argb; }
            set
            {
                _surface.SetArgb(value);
                SyncHsvFromRgb();
                SyncBoxes();
            }
        }

        /// <summary>目前是不是顯示 HSV 分頁（false = RGB 分頁）。</summary>
        public bool HsvMode
        {
            get { return _tabHsv.IsChecked == true; }
            set
            {
                if (value == HsvMode) { return; }

                _syncing = true;   // 程式端設定不要觸發 ModeChanged
                (value ? _tabHsv : _tabRgb).IsChecked = true;
                _syncing = false;
                ShowPage();
            }
        }

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            ShowPage();
            if (_syncing) { return; }

            Action handler = ModeChanged;
            if (handler != null) { handler(); }
        }

        private void ShowPage()
        {
            _rgbPage.Visibility = HsvMode ? Visibility.Collapsed : Visibility.Visible;
            _hsvPage.Visibility = HsvMode ? Visibility.Visible : Visibility.Collapsed;
        }

        private static RadioButton MakeTab(string text, string automationId)
        {
            RadioButton tab = new RadioButton { Content = text, GroupName = "ColorPickerTabs" };
            System.Windows.Automation.AutomationProperties.SetAutomationId(tab, automationId);
            Style style = Application.Current == null ? null : Application.Current.TryFindResource("TabRadio") as Style;
            if (style != null) { tab.Style = style; }
            return tab;
        }

        private static UniformGrid MakeLabels(params string[] names)
        {
            UniformGrid labels = new UniformGrid { Columns = names.Length, Margin = new Thickness(0, 6, 0, 0) };
            foreach (string s in names)
            {
                labels.Children.Add(new TextBlock { Text = s, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)) });
            }
            return labels;
        }

        private static UniformGrid MakeRow(params TextBox[] boxes)
        {
            UniformGrid row = new UniformGrid { Columns = boxes.Length };
            foreach (TextBox b in boxes) { row.Children.Add(b); }
            return row;
        }

        /// <summary>透明度用的棋盤格是淺色（白／淺灰，預設）還是深色。換用 <see cref="SetLightChecker"/>，所有顏色預覽、A 條會立刻跟著換。</summary>
        public static bool LightChecker { get; private set; } = true;

        /// <summary>換透明棋盤的深淺：更新旗標、清掉快取的筆刷，並通知所有色彩選擇器重畫。</summary>
        public static void SetLightChecker(bool light)
        {
            if (LightChecker == light) { return; }

            LightChecker = light;
            Surface.ResetChecker();
            HsvSurface.ResetChecker();
            LightCheckerChanged?.Invoke();
        }

        private static event Action LightCheckerChanged;

        private void OnLightCheckerChanged()
        {
            _preview.Background = Surface.CheckerBrush(5);
            _surface.InvalidateVisual();
            _hsv.InvalidateVisual();
        }

        /// <summary>透明度用的棋盤格筆刷（調色盤色塊也用它墊底，看得出半透明）。</summary>
        public static Brush CreateCheckerBrush(double cell)
        {
            return Surface.CheckerBrush(cell);
        }

        // --- 內部 ------------------------------------------------------------------------------------------
        private static TextBox MakeBox(string automationId)
        {
            TextBox box = new TextBox { Margin = new Thickness(1, 0, 1, 0), Padding = new Thickness(0, 1, 0, 1), TextAlignment = TextAlignment.Center, MaxLength = 3, MinHeight = 0 };   // MinHeight = 0：全域 TextBox 樣式的最小高度 26 對這四個緊湊的數值框太高
            System.Windows.Automation.AutomationProperties.SetAutomationId(box, automationId);
            return box;
        }

        private void WireBox(TextBox box, Action commit)
        {
            box.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter || e.Key == Key.Return)
                {
                    commit();
                    if (HsvMode) { _hsv.Focus(); } else { _surface.Focus(); }   // 離開輸入框（要交給目前看得到的那一頁），視窗快捷鍵才會恢復
                    e.Handled = true;
                }
            };
            box.LostFocus += (s, e) => commit();
            box.GotKeyboardFocus += (s, e) => box.SelectAll();
        }

        private void CommitBoxes()
        {
            if (_syncing) { return; }

            int r, g, b, a;
            bool okR = TryByte(_rBox.Text, out r), okG = TryByte(_gBox.Text, out g);
            bool okB = TryByte(_bBox.Text, out b), okA = TryByte(_aBox.Text, out a);

            if (!okR) { r = _surface.R; }
            if (!okG) { g = _surface.G; }
            if (!okB) { b = _surface.B; }
            if (!okA) { a = _surface.A; }
            if (a < 1) { a = 1; }

            bool changed = r != _surface.R || g != _surface.G || b != _surface.B || a != _surface.A;
            _surface.SetRgba(r, g, b, a);
            if (changed) { SyncHsvFromRgb(); }
            SyncBoxes();   // 輸入不合法時把框還原成目前值
            if (changed) { RaiseChanged(); }
        }

        /// <summary>HSV 頁的數值框：H 0~360、S 0~100、V 0~100、A 1~255；寫不合法的就還原。</summary>
        private void CommitHsvBoxes()
        {
            if (_syncing) { return; }

            int h, s, v, a;
            bool okH = TryRange(_hBox.Text, 0, 360, out h), okS = TryRange(_sBox.Text, 0, 100, out s);
            bool okV = TryRange(_vBox.Text, 0, 100, out v), okA = TryRange(_haBox.Text, 0, 255, out a);

            if (!okH) { h = (int)Math.Round(_hsv.H); }
            if (!okS) { s = (int)Math.Round(_hsv.S * 100); }
            if (!okV) { v = (int)Math.Round(_hsv.V * 100); }
            if (!okA) { a = _hsv.A; }
            if (a < 1) { a = 1; }

            bool changed = h != (int)Math.Round(_hsv.H) || s != (int)Math.Round(_hsv.S * 100) || v != (int)Math.Round(_hsv.V * 100) || a != _hsv.A;
            if (changed)
            {
                _hsv.Set(h, s / 100.0, v / 100.0, a);
                ApplyHsvToRgb();
            }
            SyncBoxes();
            if (changed) { RaiseChanged(); }
        }

        private static bool TryRange(string text, int min, int max, out int value)
        {
            if (int.TryParse(text.Trim(), out value))
            {
                if (value < min) { value = min; }
                if (value > max) { value = max; }
                return true;
            }
            return false;
        }

        /// <summary>HSV 頁改了顏色：換算成 RGB 寫進正本。（HSV 自己的 H、S、V 不重算，所以拖曳時不會因為四捨五入抖動。）</summary>
        private void ApplyHsvToRgb()
        {
            int r, g, b;
            HsvSurface.HsvToRgb(_hsv.H, _hsv.S, _hsv.V, out r, out g, out b);
            _surface.SetRgba(r, g, b, _hsv.A);
        }

        /// <summary>RGB 頁（或程式端）改了顏色：HSV 跟著重算；純灰／純黑時保留原本的色相與飽和度；顏色其實沒變就完全不動。</summary>
        private void SyncHsvFromRgb()
        {
            _hsv.SetFromRgba(_surface.R, _surface.G, _surface.B, _surface.A);
        }

        private void Hsv_Edited()
        {
            ApplyHsvToRgb();
            SyncBoxes();
            RaiseChanged();
        }

        private static bool TryByte(string text, out int value)
        {
            if (int.TryParse(text.Trim(), out value))
            {
                if (value < 0) { value = 0; }
                if (value > 255) { value = 255; }
                return true;
            }
            return false;
        }

        private void Surface_Edited()
        {
            SyncHsvFromRgb();
            SyncBoxes();
            RaiseChanged();
        }

        private void SyncBoxes()
        {
            _syncing = true;
            _rBox.Text = _surface.R.ToString();
            _gBox.Text = _surface.G.ToString();
            _bBox.Text = _surface.B.ToString();
            _aBox.Text = _surface.A.ToString();
            _hBox.Text = ((int)Math.Round(_hsv.H)).ToString();
            _sBox.Text = ((int)Math.Round(_hsv.S * 100)).ToString();
            _vBox.Text = ((int)Math.Round(_hsv.V * 100)).ToString();
            _haBox.Text = _hsv.A.ToString();
            _previewColor.Background = new SolidColorBrush(Color.FromArgb((byte)_surface.A, (byte)_surface.R, (byte)_surface.G, (byte)_surface.B));
            _syncing = false;
        }

        private void RaiseChanged()
        {
            Action handler = ColorChanged;
            if (handler != null) { handler(); }
        }

        // ============================================================================================
        // 畫面：方框 + 兩條直條，全部自己畫、自己處理滑鼠
        // ============================================================================================
        private sealed class Surface : FrameworkElement
        {
            private const double BarW = 18, Gap = 6;
            private const int SqRes = 64;   // 方框底圖解析度（拉伸顯示，漸層看不出差別）

            private enum Part { None, Square, BarB, BarA }

            private WriteableBitmap _square;
            private int _squareB = -1;
            private Part _drag = Part.None;

            public int R { get; private set; }
            public int G { get; private set; }
            public int B { get; private set; }
            public int A { get; private set; } = 255;

            public event Action Edited;

            public Surface()
            {
                Focusable = true;
                FocusVisualStyle = null;
                Cursor = Cursors.Cross;
                RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
                R = 255; G = 255; B = 255;
            }

            public int Argb
            {
                get { return (A << 24) | (R << 16) | (G << 8) | B; }
            }

            public void SetArgb(int argb)
            {
                SetRgba((argb >> 16) & 255, (argb >> 8) & 255, argb & 255, (int)((uint)argb >> 24));
            }

            public void SetRgba(int r, int g, int b, int a)
            {
                R = r; G = g; B = b; A = a < 1 ? 1 : a;
                InvalidateVisual();
            }

            // --- 版面 ---
            private double SquareSize
            {
                get { return Math.Max(20, ActualWidth - 2 * BarW - 2 * Gap); }
            }

            protected override Size MeasureOverride(Size constraint)
            {
                double w = double.IsInfinity(constraint.Width) ? 174 : constraint.Width;
                return new Size(w, Math.Max(20, w - 2 * BarW - 2 * Gap));
            }

            private Rect SquareRect { get { double s = SquareSize; return new Rect(0, 0, s, s); } }
            private Rect BarBRect { get { double s = SquareSize; return new Rect(s + Gap, 0, BarW, s); } }
            private Rect BarARect { get { double s = SquareSize; return new Rect(s + 2 * Gap + BarW, 0, BarW, s); } }

            // --- 畫 ---
            private static Brush _checker;

            public static void ResetChecker() { _checker = null; }

            public static Brush CheckerBrush(double cell)
            {
                // 跟畫布上透明格的棋盤（LayerCanvas 的 LightCheck*／DarkCheck*）同樣兩個顏色
                Brush light = new SolidColorBrush(LightChecker ? Color.FromRgb(0xFF, 0xFF, 0xFF) : Color.FromRgb(0x46, 0x46, 0x46));
                Brush dark = new SolidColorBrush(LightChecker ? Color.FromRgb(0xCC, 0xCC, 0xCC) : Color.FromRgb(0x3C, 0x3C, 0x3C));
                DrawingGroup g = new DrawingGroup();
                g.Children.Add(new GeometryDrawing(light, null, new RectangleGeometry(new Rect(0, 0, cell * 2, cell * 2))));
                g.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(0, 0, cell, cell))));
                g.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(cell, cell, cell, cell))));
                DrawingBrush brush = new DrawingBrush(g)
                {
                    TileMode = TileMode.Tile,
                    ViewportUnits = BrushMappingMode.Absolute,
                    Viewport = new Rect(0, 0, cell * 2, cell * 2),
                };
                brush.Freeze();
                return brush;
            }

            private void EnsureSquare()
            {
                if (_square != null && _squareB == B) { return; }
                if (_square == null) { _square = new WriteableBitmap(SqRes, SqRes, 96, 96, PixelFormats.Bgra32, null); }

                int[] px = new int[SqRes * SqRes];
                for (int y = 0; y < SqRes; y++)
                {
                    int g = (SqRes - 1 - y) * 255 / (SqRes - 1);   // 往上 G 越大
                    for (int x = 0; x < SqRes; x++)
                    {
                        int r = x * 255 / (SqRes - 1);
                        px[y * SqRes + x] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | B;
                    }
                }
                _square.WritePixels(new Int32Rect(0, 0, SqRes, SqRes), px, SqRes * 4, 0);
                _squareB = B;
            }

            protected override void OnRender(DrawingContext dc)
            {
                if (_checker == null) { _checker = CheckerBrush(5); }

                Rect sq = SquareRect, bb = BarBRect, ba = BarARect;
                Pen frame = new Pen(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)), 1);

                // 方框：X = R、Y = G（B 固定成目前值）
                EnsureSquare();
                dc.DrawImage(_square, sq);
                dc.DrawRectangle(null, frame, sq);

                // B 直條：上 = (R,G,255)、下 = (R,G,0)
                LinearGradientBrush bBrush = new LinearGradientBrush(
                    Color.FromRgb((byte)R, (byte)G, 255), Color.FromRgb((byte)R, (byte)G, 0), 90);
                dc.DrawRectangle(bBrush, frame, bb);

                // A 直條：棋盤格底，上面蓋 (R,G,B) 由不透明漸變成透明
                dc.DrawRectangle(_checker, null, ba);
                LinearGradientBrush aBrush = new LinearGradientBrush(
                    Color.FromArgb(255, (byte)R, (byte)G, (byte)B), Color.FromArgb(0, (byte)R, (byte)G, (byte)B), 90);
                dc.DrawRectangle(aBrush, frame, ba);

                // 標記
                double px = sq.Left + R / 255.0 * sq.Width;
                double py = sq.Top + (1 - G / 255.0) * sq.Height;
                dc.DrawEllipse(null, new Pen(Brushes.Black, 3), new Point(px, py), 5, 5);
                dc.DrawEllipse(null, new Pen(Brushes.White, 1.5), new Point(px, py), 5, 5);

                DrawBarMarker(dc, bb, B);
                DrawBarMarker(dc, ba, A);
            }

            private static void DrawBarMarker(DrawingContext dc, Rect bar, int value)
            {
                double y = bar.Top + (1 - value / 255.0) * bar.Height;
                dc.DrawRectangle(null, new Pen(Brushes.Black, 3), new Rect(bar.Left - 1, y - 2, bar.Width + 2, 4));
                dc.DrawRectangle(null, new Pen(Brushes.White, 1), new Rect(bar.Left - 1, y - 2, bar.Width + 2, 4));
            }

            // --- 滑鼠 ---
            protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
            {
                base.OnMouseLeftButtonDown(e);

                Point p = e.GetPosition(this);
                if (SquareRect.Contains(p)) { _drag = Part.Square; }
                else if (BarBRect.Contains(p)) { _drag = Part.BarB; }
                else if (BarARect.Contains(p)) { _drag = Part.BarA; }
                else { _drag = Part.None; }

                if (_drag == Part.None) { return; }

                Focus();   // 從數值輸入框移開焦點，視窗快捷鍵才會恢復
                CaptureMouse();
                Apply(p);
                e.Handled = true;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (_drag != Part.None && IsMouseCaptured) { Apply(e.GetPosition(this)); }
            }

            protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
            {
                base.OnMouseLeftButtonUp(e);
                if (_drag != Part.None)
                {
                    _drag = Part.None;
                    ReleaseMouseCapture();
                }
            }

            private void Apply(Point p)
            {
                int r = R, g = G, b = B, a = A;
                if (_drag == Part.Square)
                {
                    Rect sq = SquareRect;
                    r = ToByte((p.X - sq.Left) / sq.Width);
                    g = ToByte(1 - (p.Y - sq.Top) / sq.Height);
                }
                else if (_drag == Part.BarB)
                {
                    Rect bar = BarBRect;
                    b = ToByte(1 - (p.Y - bar.Top) / bar.Height);
                }
                else if (_drag == Part.BarA)
                {
                    Rect bar = BarARect;
                    a = Math.Max(1, ToByte(1 - (p.Y - bar.Top) / bar.Height));
                }

                if (r == R && g == G && b == B && a == A) { return; }

                R = r; G = g; B = b; A = a;
                InvalidateVisual();

                Action handler = Edited;
                if (handler != null) { handler(); }
            }

            private static int ToByte(double t)
            {
                int v = (int)Math.Round(t * 255);
                return v < 0 ? 0 : v > 255 ? 255 : v;
            }
        }

        // ============================================================================================
        // HSV 分頁：方框 X = 飽和度（右邊最飽和）、Y = 明度（上面最亮），第一條 = 色相（往上越大）、第二條 = A
        // ============================================================================================
        private sealed class HsvSurface : FrameworkElement
        {
            private const double BarW = 18, Gap = 6;
            private const int SqRes = 64;

            private enum Part { None, Square, BarH, BarA }

            private WriteableBitmap _square;
            private int _squareHue = -1;
            private Part _drag = Part.None;
            private static Brush _checker;

            public static void ResetChecker() { _checker = null; }

            /// <summary>色相 0~360（度）。</summary>
            public double H { get; private set; }
            /// <summary>飽和度 0~1。</summary>
            public double S { get; private set; }
            /// <summary>明度 0~1。</summary>
            public double V { get; private set; } = 1;
            /// <summary>透明度 1~255（跟 RGB 頁共用同一個值）。</summary>
            public int A { get; private set; } = 255;

            public event Action Edited;

            public HsvSurface()
            {
                Focusable = true;
                FocusVisualStyle = null;
                Cursor = Cursors.Cross;
                RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
            }

            public void Set(double h, double s, double v, int a)
            {
                H = Math.Max(0, Math.Min(360, h));
                S = Math.Max(0, Math.Min(1, s));
                V = Math.Max(0, Math.Min(1, v));
                A = a < 1 ? 1 : a > 255 ? 255 : a;
                InvalidateVisual();
            }

            /// <summary>
            /// 由 RGB 反推 HSV。目前的 HSV 換算出來剛好就是這個 RGB（例如使用者正在 HSV 頁拖曳）就完全不動，只更新 A，
            /// 避免四捨五入造成抖動；純灰（飽和度 0）保留色相，純黑（明度 0）保留色相和飽和度。
            /// </summary>
            public void SetFromRgba(int r, int g, int b, int a)
            {
                A = a < 1 ? 1 : a > 255 ? 255 : a;

                int cr, cg, cb;
                HsvToRgb(H, S, V, out cr, out cg, out cb);
                if (cr == r && cg == g && cb == b) { InvalidateVisual(); return; }

                int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                int delta = max - min;
                V = max / 255.0;
                if (max == 0) { InvalidateVisual(); return; }   // 純黑：保留 H、S

                S = delta / (double)max;
                if (delta != 0)
                {
                    double h;
                    if (max == r) { h = ((g - b) / (double)delta) % 6; }
                    else if (max == g) { h = (b - r) / (double)delta + 2; }
                    else { h = (r - g) / (double)delta + 4; }
                    h *= 60;
                    if (h < 0) { h += 360; }
                    H = h;
                }
                InvalidateVisual();
            }

            public static void HsvToRgb(double h, double s, double v, out int r, out int g, out int b)
            {
                h = h % 360;
                if (h < 0) { h += 360; }

                double c = v * s;
                double hp = h / 60;
                double x = c * (1 - Math.Abs(hp % 2 - 1));
                double rr, gg, bb;
                if (hp < 1) { rr = c; gg = x; bb = 0; }
                else if (hp < 2) { rr = x; gg = c; bb = 0; }
                else if (hp < 3) { rr = 0; gg = c; bb = x; }
                else if (hp < 4) { rr = 0; gg = x; bb = c; }
                else if (hp < 5) { rr = x; gg = 0; bb = c; }
                else { rr = c; gg = 0; bb = x; }

                double m = v - c;
                r = (int)Math.Round((rr + m) * 255);
                g = (int)Math.Round((gg + m) * 255);
                b = (int)Math.Round((bb + m) * 255);
            }

            // --- 版面（跟 RGB 頁一樣）---
            private double SquareSize { get { return Math.Max(20, ActualWidth - 2 * BarW - 2 * Gap); } }

            protected override Size MeasureOverride(Size constraint)
            {
                double w = double.IsInfinity(constraint.Width) ? 174 : constraint.Width;
                return new Size(w, Math.Max(20, w - 2 * BarW - 2 * Gap));
            }

            private Rect SquareRect { get { double s = SquareSize; return new Rect(0, 0, s, s); } }
            private Rect BarHRect { get { double s = SquareSize; return new Rect(s + Gap, 0, BarW, s); } }
            private Rect BarARect { get { double s = SquareSize; return new Rect(s + 2 * Gap + BarW, 0, BarW, s); } }

            private void EnsureSquare()
            {
                int hue = (int)Math.Round(H);
                if (_square != null && _squareHue == hue) { return; }
                if (_square == null) { _square = new WriteableBitmap(SqRes, SqRes, 96, 96, PixelFormats.Bgra32, null); }

                int[] px = new int[SqRes * SqRes];
                for (int y = 0; y < SqRes; y++)
                {
                    double v = 1 - y / (double)(SqRes - 1);
                    for (int x = 0; x < SqRes; x++)
                    {
                        int r, g, b;
                        HsvToRgb(hue, x / (double)(SqRes - 1), v, out r, out g, out b);
                        px[y * SqRes + x] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
                    }
                }
                _square.WritePixels(new Int32Rect(0, 0, SqRes, SqRes), px, SqRes * 4, 0);
                _squareHue = hue;
            }

            protected override void OnRender(DrawingContext dc)
            {
                if (_checker == null) { _checker = Surface.CheckerBrush(5); }

                Rect sq = SquareRect, bh = BarHRect, ba = BarARect;
                Pen frame = new Pen(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)), 1);

                EnsureSquare();
                dc.DrawImage(_square, sq);
                dc.DrawRectangle(null, frame, sq);

                // 色相直條：上 = 360°（紅）、往下依序 300（洋紅）、240（藍）、180（青）、120（綠）、60（黃）、0（紅）
                LinearGradientBrush hueBrush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                Color[] stops = { Colors.Red, Colors.Magenta, Colors.Blue, Colors.Cyan, Colors.Lime, Colors.Yellow, Colors.Red };
                for (int i = 0; i < stops.Length; i++) { hueBrush.GradientStops.Add(new GradientStop(stops[i], i / 6.0)); }
                dc.DrawRectangle(hueBrush, frame, bh);

                // A 直條：棋盤格底，上面蓋目前顏色由不透明漸變成透明
                int r, g, b;
                HsvToRgb(H, S, V, out r, out g, out b);
                dc.DrawRectangle(_checker, null, ba);
                LinearGradientBrush aBrush = new LinearGradientBrush(
                    Color.FromArgb(255, (byte)r, (byte)g, (byte)b), Color.FromArgb(0, (byte)r, (byte)g, (byte)b), 90);
                dc.DrawRectangle(aBrush, frame, ba);

                double px = sq.Left + S * sq.Width;
                double py = sq.Top + (1 - V) * sq.Height;
                dc.DrawEllipse(null, new Pen(Brushes.Black, 3), new Point(px, py), 5, 5);
                dc.DrawEllipse(null, new Pen(Brushes.White, 1.5), new Point(px, py), 5, 5);

                DrawBarMarker(dc, bh, H / 360.0);
                DrawBarMarker(dc, ba, A / 255.0);
            }

            private static void DrawBarMarker(DrawingContext dc, Rect bar, double t)
            {
                double y = bar.Top + (1 - t) * bar.Height;
                dc.DrawRectangle(null, new Pen(Brushes.Black, 3), new Rect(bar.Left - 1, y - 2, bar.Width + 2, 4));
                dc.DrawRectangle(null, new Pen(Brushes.White, 1), new Rect(bar.Left - 1, y - 2, bar.Width + 2, 4));
            }

            // --- 滑鼠 ---
            protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
            {
                base.OnMouseLeftButtonDown(e);

                Point p = e.GetPosition(this);
                if (SquareRect.Contains(p)) { _drag = Part.Square; }
                else if (BarHRect.Contains(p)) { _drag = Part.BarH; }
                else if (BarARect.Contains(p)) { _drag = Part.BarA; }
                else { _drag = Part.None; }

                if (_drag == Part.None) { return; }

                Focus();
                CaptureMouse();
                Apply(p);
                e.Handled = true;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (_drag != Part.None && IsMouseCaptured) { Apply(e.GetPosition(this)); }
            }

            protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
            {
                base.OnMouseLeftButtonUp(e);
                if (_drag != Part.None)
                {
                    _drag = Part.None;
                    ReleaseMouseCapture();
                }
            }

            private static double Clamp01(double t) { return t < 0 ? 0 : t > 1 ? 1 : t; }

            private void Apply(Point p)
            {
                double h = H, s = S, v = V;
                int a = A;
                if (_drag == Part.Square)
                {
                    Rect sq = SquareRect;
                    s = Clamp01((p.X - sq.Left) / sq.Width);
                    v = Clamp01(1 - (p.Y - sq.Top) / sq.Height);
                }
                else if (_drag == Part.BarH)
                {
                    Rect bar = BarHRect;
                    h = Math.Round(Clamp01(1 - (p.Y - bar.Top) / bar.Height) * 360);
                }
                else if (_drag == Part.BarA)
                {
                    Rect bar = BarARect;
                    a = Math.Max(1, (int)Math.Round(Clamp01(1 - (p.Y - bar.Top) / bar.Height) * 255));
                }

                if (h == H && s == S && v == V && a == A) { return; }

                H = h; S = s; V = v; A = a;
                InvalidateVisual();

                Action handler = Edited;
                if (handler != null) { handler(); }
            }
        }
    }
}
