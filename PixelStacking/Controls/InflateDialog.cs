using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PixelStacking.Data;
using PixelStacking.Render;

namespace PixelStacking.Controls
{
    /// <summary>
    /// 「膨脹成 3D」對話框：把一張 2D 圖片的輪廓往厚度方向鼓起來變成立體。左邊是參數，右邊是即時預覽（可以拖曳滑桿轉角度看）。
    /// 按「膨脹」只回傳 <see cref="Options"/>，實際寫入模型由呼叫端做（才能算進復原）。
    /// </summary>
    public sealed class InflateDialog : Window
    {
        private static readonly ViewDirection[] Views = { ViewDirection.Top, ViewDirection.Bottom, ViewDirection.Front, ViewDirection.Back, ViewDirection.Left, ViewDirection.Right };

        private readonly LoadedImage _image;
        private readonly int _w, _h, _d;
        private readonly ComboBox _view = new ComboBox { Width = 230 };
        private readonly ComboBox _fit = new ComboBox { Width = 230 };
        private readonly ComboBox _profile = new ComboBox { Width = 230 };
        private readonly ComboBox _side = new ComboBox { Width = 230 };
        private readonly CheckBox _auto = new CheckBox { Content = LangResources.Get(TextKey.Inflate_AutoThickestPointLargestInscribed), IsChecked = true, Margin = new Thickness(0, 4, 0, 2) };
        private readonly CheckBox _clear = new CheckBox { Content = LangResources.Get(TextKey.Inflate_ClearThisFrameBeforeApplying), IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
        private readonly Slider _threshold, _maxDepth, _minDepth, _radius, _smooth, _yawSlider;
        private readonly TextBlock _thresholdText = new TextBlock(), _maxDepthText = new TextBlock(), _minDepthText = new TextBlock(), _radiusText = new TextBlock(), _smoothText = new TextBlock();
        private readonly TextBlock _info = new TextBlock { FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)), TextWrapping = TextWrapping.Wrap, MaxWidth = 300, Margin = new Thickness(0, 6, 0, 0) };
        private readonly System.Windows.Controls.Image _preview = new System.Windows.Controls.Image { Width = 300, Height = 300, Stretch = Stretch.Uniform };
        private readonly StackRenderer _renderer = new StackRenderer();
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };

        private Animation _tmp;
        private string _cacheKey;
        private byte[] _indices;
        private int _cw, _ch, _count;

        public InflateOptions Options { get; private set; }

        /// <param name="image">已經讀好的圖片。</param>
        /// <param name="fileInfo">給使用者看的檔名與尺寸。</param>
        /// <param name="w">目前模型的長、寬、高（預覽用同樣大小的暫存模型）。</param>
        public InflateDialog(LoadedImage image, string fileInfo, int w, int h, int d, ViewDirection defaultView)
        {
            DialogStyle.Apply(this);
            _image = image; _w = w; _h = h; _d = d;
            Title = LangResources.Get(TextKey.Common_InflateTo3D);
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
            Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));

            foreach (ViewDirection v in Views) { _view.Items.Add(LangResources.Format(TextKey.Inflate_ViewOptionWithAxis, ViewMapper.Name(v), ViewMapper.SliceAxis(v))); }
            _view.SelectedIndex = Array.IndexOf(Views, defaultView);
            _fit.Items.Add(LangResources.Get(TextKey.Inflate_CenteredAnythingOutsideIsCropped)); _fit.Items.Add(LangResources.Get(TextKey.Common_AlignTopLeft)); _fit.Items.Add(LangResources.Get(TextKey.Inflate_StretchToTheCanvasSize)); _fit.Items.Add(LangResources.Get(TextKey.Inflate_ScaleProportionallyToFitThe));
            _fit.SelectedIndex = 3;
            _profile.Items.Add(LangResources.Get(TextKey.Inflate_RoundLikeABalloon)); _profile.Items.Add(LangResources.Get(TextKey.Inflate_SmoothSCurve)); _profile.Items.Add(LangResources.Get(TextKey.Inflate_ConeLikeARoof)); _profile.Items.Add(LangResources.Get(TextKey.Inflate_FlatJustExtruded));
            _profile.SelectedIndex = 0;
            _side.Items.Add(LangResources.Get(TextKey.Inflate_SymmetricFrontAndBack)); _side.Items.Add(LangResources.Get(TextKey.Inflate_BulgeOnlyTowardTheViewer)); _side.Items.Add(LangResources.Get(TextKey.Inflate_BulgeOnlyAwayFromThe));
            _side.SelectedIndex = 0;

            int maxHalf = Math.Max(2, Math.Max(w, Math.Max(h, d)) / 2);
            _threshold = MakeSlider(1, 255, 128, _thresholdText, v => v.ToString());
            _maxDepth = MakeSlider(1, Math.Max(1, d), Math.Min(Math.Max(1, d), 12), _maxDepthText, v => LangResources.Format(TextKey.Inflate_Cells, v));
            _minDepth = MakeSlider(1, 8, 2, _minDepthText, v => LangResources.Format(TextKey.Inflate_Cells, v));
            _radius = MakeSlider(0, maxHalf, 0, _radiusText, v => v == 0 ? LangResources.Get(TextKey.Inflate_Auto) : LangResources.Format(TextKey.Inflate_Cells, v));
            _smooth = MakeSlider(0, 6, 1, _smoothText, v => v.ToString());
            _yawSlider = new Slider { Minimum = 0, Maximum = 360, Value = 35, Width = 300, Margin = new Thickness(0, 6, 0, 0) };
            _yawSlider.ValueChanged += (s, e) => Schedule();
            _maxDepth.IsEnabled = false;

            SetId(_view, "InfView"); SetId(_fit, "InfFit"); SetId(_profile, "InfProfile"); SetId(_side, "InfSide"); SetId(_auto, "InfAuto"); SetId(_clear, "InfClear");
            SetId(_threshold, "InfThreshold"); SetId(_maxDepth, "InfMaxDepth"); SetId(_minDepth, "InfMinDepth"); SetId(_radius, "InfRadius"); SetId(_smooth, "InfSmooth"); SetId(_yawSlider, "InfYaw"); SetId(_info, "InfInfo");
            foreach (ComboBox c in new[] { _view, _fit, _profile, _side }) { c.SelectionChanged += (s, e) => Schedule(); }
            _auto.Checked += (s, e) => { _maxDepth.IsEnabled = false; Schedule(); };
            _auto.Unchecked += (s, e) => { _maxDepth.IsEnabled = true; Schedule(); };

            StackPanel left = new StackPanel { Width = 330, Margin = new Thickness(0, 0, 16, 0) };
            left.Children.Add(new TextBlock { Text = fileInfo, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)), TextWrapping = TextWrapping.Wrap, MaxWidth = 330 });
            left.Children.Add(Title2(LangResources.Get(TextKey.Inflate_WhichViewTheImageIs)));
            left.Children.Add(_view);
            left.Children.Add(_fit);
            left.Children.Add(SliderRow(LangResources.Get(TextKey.Inflate_TransparencyThresholdAlpha), _threshold, _thresholdText));
            left.Children.Add(Title2(LangResources.Get(TextKey.Inflate_HowItBulges)));
            left.Children.Add(_profile);
            left.Children.Add(_side);
            left.Children.Add(_auto);
            left.Children.Add(SliderRow(LangResources.Get(TextKey.Inflate_Thickest), _maxDepth, _maxDepthText));
            left.Children.Add(SliderRow(LangResources.Get(TextKey.Inflate_ThinnestEdge), _minDepth, _minDepthText));
            left.Children.Add(SliderRow(LangResources.Get(TextKey.Inflate_DistanceToReachTheThickest), _radius, _radiusText));
            left.Children.Add(SliderRow(LangResources.Get(TextKey.Inflate_Smoothing), _smooth, _smoothText));
            left.Children.Add(_clear);

            StackPanel right = new StackPanel();
            right.Children.Add(Title2(LangResources.Get(TextKey.Inflate_PreviewDragTheSliderBelow)));
            Border frame = new Border { Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)), BorderThickness = new Thickness(1), Child = _preview, Width = 302, Height = 302 };
            right.Children.Add(frame);
            right.Children.Add(_yawSlider);
            right.Children.Add(_info);

            Button ok = new Button { Content = LangResources.Get(TextKey.Inflate_Inflate), Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            Button cancel = new Button { Content = LangResources.Get(TextKey.Common_Cancel), Width = 90, IsCancel = true };
            SetId(ok, "InfOk"); SetId(cancel, "InfCancel");
            ok.Click += (s, e) => { Options = Collect(); DialogResult = true; };
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            buttons.Children.Add(ok); buttons.Children.Add(cancel);

            Grid body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(right, 1);
            body.Children.Add(left); body.Children.Add(right);

            StackPanel root = new StackPanel { Margin = new Thickness(16) };
            root.Children.Add(body);
            root.Children.Add(buttons);
            Content = root;

            _timer.Tick += (s, e) => { _timer.Stop(); Refresh(); };
            Loaded += (s, e) => Refresh();
        }

        private Slider MakeSlider(double min, double max, double value, TextBlock label, Func<int, string> fmt)
        {
            Slider s = new Slider { Minimum = min, Maximum = max, Value = value, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 160, VerticalAlignment = VerticalAlignment.Center };
            label.Text = fmt((int)value);
            s.ValueChanged += (a, b) => { label.Text = fmt((int)s.Value); Schedule(); };
            return s;
        }

        private static FrameworkElement SliderRow(string name, Slider s, TextBlock value)
        {
            Grid g = new Grid { Margin = new Thickness(0, 3, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            g.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(s, 1); g.Children.Add(s);
            value.VerticalAlignment = VerticalAlignment.Center; value.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(value, 2); g.Children.Add(value);
            return g;
        }

        private static void SetId(DependencyObject o, string id) { AutomationProperties.SetAutomationId(o, id); }

        private static TextBlock Title2(string text)
        {
            return new TextBlock { Text = text, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 8, 0, 4), Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xDC, 0xFE)) };
        }

        private void Schedule() { _timer.Stop(); _timer.Start(); }

        /// <summary>依目前的控制項讀出選項。</summary>
        public InflateOptions Collect()
        {
            return new InflateOptions
            {
                View = Views[Math.Max(0, _view.SelectedIndex)],
                Fit = _fit.SelectedIndex == 0 ? ImageFit.Center : _fit.SelectedIndex == 1 ? ImageFit.TopLeft : _fit.SelectedIndex == 2 ? ImageFit.Stretch : ImageFit.Contain,
                AlphaThreshold = (int)_threshold.Value,
                Profile = (InflateProfile)Math.Max(0, _profile.SelectedIndex),
                Side = (InflateSide)Math.Max(0, _side.SelectedIndex),
                AutoDepth = _auto.IsChecked == true,
                MaxDepth = (int)_maxDepth.Value,
                MinDepth = (int)_minDepth.Value,
                Radius = _radius.Value,
                Smooth = (int)_smooth.Value,
                Clear = _clear.IsChecked == true,
            };
        }

        /// <summary>重算預覽。公開給測試用（不用等計時器）。</summary>
        public void Refresh()
        {
            InflateOptions o = Collect();
            _cw = ViewMapper.CanvasWidth(o.View, _w, _h, _d); _ch = ViewMapper.CanvasHeight(o.View, _w, _h, _d); _count = ViewMapper.SliceCount(o.View, _w, _h, _d);

            // 顏色對應只跟視角、放法、門檻有關：這三個沒變就沿用，拖厚度滑桿時才不會每次重算量化
            string key = $"{o.View}|{o.Fit}|{o.AlphaThreshold}";
            if (key != _cacheKey || _tmp == null)
            {
                _tmp = new Animation(_w, _h, _d);
                int[] canvas = Inflate.PrepareCanvas(_image, _cw, _ch, o.Fit, o.AlphaThreshold);
                ImageImport.MapStats stats;
                _indices = ImageImport.MapToPalette(_tmp, new List<int[]> { canvas }, out stats)[0];
                _cacheKey = key;
                _renderer.Configure(_w, _h, _d, 300);
                _preview.Source = _renderer.Bitmap;
            }

            int[] thickness = Inflate.Thickness(_indices, _cw, _ch, _count, o);
            int written = Inflate.Fill(_tmp.Frames[0], o.View, _indices, thickness, o.Side, true);
            _tmp.Frames[0].NotifyChanged();

            double yaw = _yawSlider.Value * Math.PI / 180;
            _renderer.Render(_tmp.Frames[0], yaw, 25 * Math.PI / 180, ClipRange.Full(_w, _h, _d), true);

            int thickest = 0; foreach (int n in thickness) { if (n > thickest) { thickest = n; } }
            _info.Text = written == 0
                ? LangResources.Get(TextKey.Inflate_TheImageHasNoColoured)
                : LangResources.Format(TextKey.Inflate_Summary, _cw, _ch, _count, written, thickest);
        }
    }
}
