using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelStacking.Data;

namespace PixelStacking.Controls
{
    /// <summary>
    /// 層數滑桿下面的「每層縮圖列」：目前視角的每一層（切片）各一張小圖，跟時間軸一樣可以點。
    /// 空的層（整層都沒有顏色）會用橘色框＋「空」標出來，一眼就看得出哪些層還沒畫。
    /// 藍框 = 目前編輯的那一層。縮圖只有在模型內容或調色盤變動時才重畫，所以每個螢幕幀呼叫 <see cref="Refresh"/> 很便宜。
    /// </summary>
    public sealed class LayerStrip : ScrollViewer
    {
        private const int ThumbSize = 48;
        private const uint BackArgb = 0xFF2A2A2A;   // 縮圖底色：透明的格子顯示成這個深灰

        private static readonly Brush NormalBorder = Freeze(new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)));
        private static readonly Brush SelectedBorder = Freeze(new SolidColorBrush(Color.FromRgb(0x37, 0x94, 0xFF)));
        private static readonly Brush EmptyBorder = Freeze(new SolidColorBrush(Color.FromRgb(0xB0, 0x6A, 0x1C)));
        private static readonly Brush NormalBack = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)));
        private static readonly Brush EmptyBack = Freeze(new SolidColorBrush(Color.FromRgb(0x2B, 0x22, 0x16)));
        private static readonly Brush NormalText = Freeze(new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB)));
        private static readonly Brush EmptyText = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x4D)));

        private sealed class Item
        {
            public Border Box;
            public TextBlock Label;
            public System.Windows.Controls.Image Image;
            public WriteableBitmap Bitmap;
            public int[] Pixels;
            public bool Empty = true;
        }

        private readonly StackPanel _panel = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly List<Item> _items = new List<Item>();
        private readonly int[] _palCopy = new int[256];
        private VoxelModel _model;
        private ViewDirection _view = ViewDirection.Top;
        private int _slice;
        private int _drawnVersion = -1;
        private bool _drawnAny;
        private int _builtCount = -1, _builtW = -1, _builtH = -1;

        /// <summary>使用者點了某一層的縮圖（參數 = 切片索引）。</summary>
        public event Action<int> SliceClicked;

        /// <summary>使用者把某一層的縮圖拖到別的位置放開（參數 = 原本的索引、放到的索引）。這個控制項只回報，資料由接收端搬。</summary>
        public event Action<int, int> SliceMoved;

        private static readonly Brush DropBorder = Freeze(new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0)));
        private const double DragThreshold = 6;
        private int _pressIndex = -1;        // 按下的是哪一層（-1 = 沒有在按）
        private Point _pressPoint;
        private bool _dragging;
        private int _dragTarget = -1;
        private readonly System.Windows.Threading.DispatcherTimer _scrollTimer =
            new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };

        /// <summary>現在是不是正在拖曳縮圖（測試與接收端判斷用）。</summary>
        public bool IsDragging => _dragging;

        public LayerStrip()
        {
            Content = _panel;
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _scrollTimer.Tick += (s, e) => AutoScrollStep();
        }

        private static Brush Freeze(Brush brush)
        {
            brush.Freeze();
            return brush;
        }

        public VoxelModel Model
        {
            get => _model;
            set
            {
                if (ReferenceEquals(_model, value)) { return; }

                _model = value;
                Invalidate();
            }
        }

        public ViewDirection View
        {
            get => _view;
            set
            {
                if (_view == value) { return; }

                _view = value;
                Invalidate();
            }
        }

        public int Slice
        {
            get => _slice;
            set
            {
                if (_slice == value) { return; }

                _slice = value;
                UpdateHighlights();
                ScrollSelectedIntoView();
            }
        }

        /// <summary>這個視角的層數。</summary>
        public int Count => _items.Count;

        /// <summary>目前視角、整層都沒有顏色的層數（要先呼叫過 <see cref="Refresh"/> 才是最新的）。</summary>
        public int EmptyCount
        {
            get
            {
                int n = 0;
                foreach (Item item in _items) { if (item.Empty) { n++; } }
                return n;
            }
        }

        /// <summary>第 index 層是不是空的。</summary>
        public bool IsEmpty(int index) { return index >= 0 && index < _items.Count && _items[index].Empty; }

        /// <summary>換模型或視角後，下一次 <see cref="Refresh"/> 一定會重畫。</summary>
        private void Invalidate()
        {
            _drawnAny = false;
            Refresh();
        }

        private void Build()
        {
            _panel.Children.Clear();
            _items.Clear();
            _builtCount = _builtW = _builtH = -1;
            if (_model == null) { return; }

            int w = _model.Width, h = _model.Height, d = _model.Depth;
            int count = ViewMapper.SliceCount(_view, w, h, d);
            int cw = ViewMapper.CanvasWidth(_view, w, h, d);
            int ch = ViewMapper.CanvasHeight(_view, w, h, d);

            for (int s = 0; s < count; s++)
            {
                Item item = new Item { Pixels = new int[cw * ch], Bitmap = new WriteableBitmap(cw, ch, 96, 96, PixelFormats.Bgra32, null) };
                item.Image = new System.Windows.Controls.Image
                {
                    Width = ThumbSize,
                    Height = ThumbSize,
                    Stretch = Stretch.Uniform,
                    Source = item.Bitmap,
                };
                RenderOptions.SetBitmapScalingMode(item.Image, BitmapScalingMode.NearestNeighbor);

                item.Label = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, FontSize = 11 };

                StackPanel stack = new StackPanel();
                stack.Children.Add(item.Image);
                stack.Children.Add(item.Label);

                item.Box = new Border
                {
                    Margin = new Thickness(2),
                    Padding = new Thickness(2),
                    BorderThickness = new Thickness(2),
                    Child = stack,
                };
                int index = s;
                item.Box.MouseLeftButtonDown += (sender, e) =>
                {
                    // 按下 = 先選這一層；不放開、移動超過一小段距離 = 開始拖曳，放開時搬到滑鼠所在的位置
                    _pressIndex = index;
                    _pressPoint = e.GetPosition(this);
                    _dragging = false;
                    _dragTarget = -1;
                    CaptureMouse();
                    SliceClicked?.Invoke(index);
                    e.Handled = true;
                };
                _items.Add(item);
                _panel.Children.Add(item.Box);
            }

            _builtCount = count; _builtW = cw; _builtH = ch;
            if (_slice >= count) { _slice = count - 1; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_pressIndex < 0) { return; }
            if (e.LeftButton != MouseButtonState.Pressed) { EndDrag(false); return; }

            if (!_dragging)
            {
                Vector d = e.GetPosition(this) - _pressPoint;
                if (Math.Abs(d.X) < DragThreshold && Math.Abs(d.Y) < DragThreshold) { return; }

                _dragging = true;
                _scrollTimer.Start();
            }
            UpdateDragTarget(e.GetPosition(_panel));
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (_pressIndex >= 0) { EndDrag(true); }
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            if (_pressIndex >= 0) { EndDrag(false); }   // 被別的東西搶走滑鼠（例如視窗失去焦點）：取消，不搬
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key == Key.Escape && _pressIndex >= 0) { EndDrag(false); e.Handled = true; }
        }

        /// <summary>滑鼠在第幾張縮圖上（超出兩端就夾到頭／尾）。x 是 _panel 座標。</summary>
        private int IndexAt(double x)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                FrameworkElement box = _items[i].Box;
                double left = box.TranslatePoint(new Point(0, 0), _panel).X;
                if (x < left + box.ActualWidth + box.Margin.Right) { return i; }
            }
            return _items.Count - 1;
        }

        private void UpdateDragTarget(Point inPanel)
        {
            int target = _items.Count == 0 ? -1 : IndexAt(inPanel.X);
            if (target == _dragTarget) { return; }

            _dragTarget = target;
            UpdateHighlights();
        }

        private void AutoScrollStep()
        {
            if (!_dragging) { return; }

            // 拖到邊邊時自動捲動，這樣才拖得到畫面外的層
            Point p = Mouse.GetPosition(this);
            const double Edge = 28;
            if (p.X < Edge) { ScrollToHorizontalOffset(Math.Max(0, HorizontalOffset - 16)); }
            else if (p.X > ActualWidth - Edge) { ScrollToHorizontalOffset(HorizontalOffset + 16); }
            UpdateDragTarget(Mouse.GetPosition(_panel));
        }

        private void EndDrag(bool commit)
        {
            int from = _pressIndex, to = _dragTarget;
            bool wasDragging = _dragging;
            _pressIndex = -1;
            _dragging = false;
            _dragTarget = -1;
            _scrollTimer.Stop();
            if (IsMouseCaptured) { ReleaseMouseCapture(); }
            UpdateHighlights();

            if (commit && wasDragging && from >= 0 && to >= 0 && to != from) { SliceMoved?.Invoke(from, to); }
        }

        private bool PaletteChanged()
        {
            int[] pal = _model.Palette;
            for (int i = 0; i < 256; i++) { if (_palCopy[i] != pal[i]) { return true; } }
            return false;
        }

        /// <summary>內容或調色盤有變動才重畫全部縮圖。每個螢幕幀呼叫一次即可。</summary>
        public void Refresh()
        {
            if (_model == null)
            {
                if (_items.Count > 0) { Build(); }
                return;
            }

            int w = _model.Width, h = _model.Height, d = _model.Depth;
            if (_builtCount != ViewMapper.SliceCount(_view, w, h, d)
                || _builtW != ViewMapper.CanvasWidth(_view, w, h, d)
                || _builtH != ViewMapper.CanvasHeight(_view, w, h, d))
            {
                Build();
                _drawnAny = false;
            }

            if (_drawnAny && _drawnVersion == _model.Version && !PaletteChanged()) { return; }

            Draw();
            _drawnVersion = _model.Version;
            _drawnAny = true;
            Array.Copy(_model.Palette, _palCopy, 256);
            UpdateHighlights();
        }

        private void Draw()
        {
            int w = _model.Width, h = _model.Height, d = _model.Depth;
            int cw = ViewMapper.CanvasWidth(_view, w, h, d);
            int ch = ViewMapper.CanvasHeight(_view, w, h, d);
            int[] pal = _model.Palette;
            byte[] data = _model.Data;

            // 調色盤每格事先跟底色混好（半透明顏色混成不透明），畫的時候就是查表
            int[] flat = new int[256];
            for (int i = 0; i < 256; i++) { flat[i] = Flatten(pal[i]); }
            int back = unchecked((int)BackArgb);

            for (int s = 0; s < _items.Count; s++)
            {
                Item item = _items[s];
                int origin, stepX, stepY;
                ViewMapper.Plane(_view, s, w, h, d, out origin, out stepX, out stepY);

                bool any = false;
                int p = 0;
                for (int cy = 0; cy < ch; cy++)
                {
                    int row = origin + cy * stepY;
                    for (int cx = 0; cx < cw; cx++)
                    {
                        byte v = data[row + cx * stepX];
                        if (v == 0) { item.Pixels[p++] = back; }
                        else { item.Pixels[p++] = flat[v]; any = true; }
                    }
                }

                item.Empty = !any;
                item.Bitmap.WritePixels(new Int32Rect(0, 0, cw, ch), item.Pixels, cw * 4, 0);
            }
        }

        private static int Flatten(int argb)
        {
            uint u = unchecked((uint)argb);
            int a = (int)(u >> 24);
            if (a == 255) { return argb; }
            if (a == 0) { return unchecked((int)BackArgb); }

            uint b = BackArgb;
            int r = (int)((((u >> 16) & 0xFF) * a + ((b >> 16) & 0xFF) * (255 - a)) / 255);
            int g = (int)((((u >> 8) & 0xFF) * a + ((b >> 8) & 0xFF) * (255 - a)) / 255);
            int bl = (int)(((u & 0xFF) * a + (b & 0xFF) * (255 - a)) / 255);
            return unchecked((int)(0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)bl));
        }

        private void UpdateHighlights()
        {
            for (int i = 0; i < _items.Count; i++)
            {
                Item item = _items[i];
                bool selected = i == _slice;
                bool empty = item.Empty;

                bool dropHere = _dragging && i == _dragTarget && i != _pressIndex;
                item.Box.BorderBrush = dropHere ? DropBorder : (selected ? SelectedBorder : (empty ? EmptyBorder : NormalBorder));
                item.Box.Opacity = (_dragging && i == _pressIndex) ? 0.45 : 1.0;   // 被拖的那張變淡，綠框 = 放開會放到這裡
                item.Box.Background = empty ? EmptyBack : NormalBack;
                item.Label.Foreground = empty ? EmptyText : NormalText;
                item.Label.Text = empty ? LangResources.Format(TextKey.Strip_SliceEmpty, i + 1) : (i + 1).ToString();
                item.Image.Opacity = empty ? 0.45 : 1.0;
            }
        }

        private void ScrollSelectedIntoView()
        {
            if (_slice >= 0 && _slice < _items.Count) { _items[_slice].Box.BringIntoView(); }
        }
    }
}
