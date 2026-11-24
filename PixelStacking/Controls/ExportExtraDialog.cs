using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using PixelStacking.Data;
using PixelStacking.Render;

namespace PixelStacking.Controls
{
    public enum ExtraExportMode { Gif, Strip }

    /// <summary>
    /// 「匯出 GIF」與「匯出疊層條帶」共用的對話框（欄位依模式不同）。
    /// GIF：固定角度的動畫 GIF（速度 = 動畫 fps）；疊層條帶：Sprite Stacking 原始格式（每層一張平面圖排成一條，每幀一列）。
    /// 匯出在背景執行緒進行（有進度條、可取消）。
    /// </summary>
    public sealed class ExportExtraDialog : Window
    {
        private readonly Animation _anim;
        private readonly ExtraExportMode _mode;
        private readonly TextBox _path = new TextBox { MinWidth = 300 };
        private readonly TextBox _yaw = Box(70), _elev = Box(70), _padding = Box(50);
        private readonly ComboBox _scale = new ComboBox { Width = 60, IsEditable = true, Margin = new Thickness(6, 0, 6, 0) };
        private readonly ComboBox _bg = new ComboBox { Width = 90, Margin = new Thickness(6, 0, 6, 0) };
        private readonly CheckBox _shade = new CheckBox { Content = LangResources.Get(TextKey.Common_ShadingLightAndDarkBy), Margin = new Thickness(0, 4, 0, 0) };
        private readonly CheckBox _outline = new CheckBox { Content = LangResources.Get(TextKey.Common_ExportOutline), Margin = new Thickness(0, 4, 0, 0) };
        private readonly OutlineOptions _outlineSettings;
        private readonly CheckBox _trim = new CheckBox { Content = LangResources.Get(TextKey.ExportExtra_TrimTheCommonEmptyMargin), IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
        private readonly CheckBox _loop = new CheckBox { Content = LangResources.Get(TextKey.ExportExtra_LoopForeverUncheckedPlayOnce), IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
        private readonly CheckBox _topFirst = new CheckBox { Content = LangResources.Get(TextKey.ExportExtra_TopLayerGoesLeftmostDefault), Margin = new Thickness(0, 4, 0, 0) };
        private readonly TextBlock _summary = new TextBlock { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, MinHeight = 34 };
        private readonly TextBlock _status = new TextBlock { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, MinHeight = 34, MaxWidth = 460 };
        private readonly ProgressBar _progress = new ProgressBar { Height = 8, Margin = new Thickness(0, 6, 0, 0), Minimum = 0, Maximum = 1 };
        private readonly Button _ok = new Button { Content = LangResources.Get(TextKey.Common_Export), Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        private readonly Button _cancel = new Button { Content = LangResources.Get(TextKey.Tip_Close), Width = 80, IsCancel = true };
        private readonly Button _browse = new Button { Content = LangResources.Get(TextKey.Common_ChooseLocation), Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 1, 8, 1) };
        private readonly Panel _inputs;

        private bool _running;
        private volatile bool _cancelRequested;

        /// <summary>最近一次匯出成功的檔案路徑（沒成功過 = null）。</summary>
        public string LastPath { get; private set; }
        /// <summary>最近一次匯出成功的描述。</summary>
        public string LastDescription { get; private set; }

        /// <summary>背景選項的文字（每次開對話框時取目前語言，不能在靜態欄位裡取一次就固定住）。順序要跟 <see cref="BgColours"/> 一致。</summary>
        private static string[] BgNames()
        {
            return new[] { LangResources.Get(TextKey.ExportExtra_Transparent), LangResources.Get(TextKey.ExportExtra_White), LangResources.Get(TextKey.ExportExtra_Black), LangResources.Get(TextKey.ExportExtra_DarkGray) };
        }
        private static readonly int[] BgColours = { 0xFFFFFF, 0xFFFFFF, 0x000000, 0x252526 };

        public ExportExtraDialog(Animation anim, ExtraExportMode mode, double yawDegrees, double elevationDegrees, bool shade, string suggestedPath)
            : this(anim, mode, yawDegrees, elevationDegrees, shade, suggestedPath, null, false) { }

        /// <param name="outline">輪廓線設定（null = 不提供；疊層條帶是平面圖不渲染，沒有輪廓線）；<paramref name="outlineOn"/> = 勾選的預設值。</param>
        public ExportExtraDialog(Animation anim, ExtraExportMode mode, double yawDegrees, double elevationDegrees, bool shade, string suggestedPath, OutlineOptions outline, bool outlineOn)
        {
            _anim = anim;
            _mode = mode;
            _outlineSettings = outline;
            DialogStyle.Apply(this);
            Title = mode == ExtraExportMode.Gif ? LangResources.Get(TextKey.ExportExtra_ExportGIFAnimationAtA) : LangResources.Get(TextKey.ExportExtra_ExportStackStripSpriteStacking);
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
            Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));

            _path.Text = suggestedPath ?? "";
            _yaw.Text = Math.Round(yawDegrees, 1).ToString(CultureInfo.InvariantCulture);
            _elev.Text = Math.Round(Math.Max(-90, Math.Min(90, elevationDegrees)), 1).ToString(CultureInfo.InvariantCulture);
            _padding.Text = "0";
            foreach (string s in new[] { "1", "2", "3", "4", "6", "8" }) { _scale.Items.Add(s); }
            _scale.Text = mode == ExtraExportMode.Gif ? "4" : "4";
            foreach (string s in BgNames()) { _bg.Items.Add(s); }
            _bg.SelectedIndex = 0;
            _shade.IsChecked = shade;
            _outline.IsChecked = outline != null && outlineOn;

            Id(_path, "ExPath"); Id(_yaw, "ExYaw"); Id(_elev, "ExElev"); Id(_padding, "ExPad"); Id(_scale, "ExScale"); Id(_bg, "ExBg");
            Id(_shade, "ExShade"); Id(_outline, "ExOutline"); Id(_trim, "ExTrim"); Id(_loop, "ExLoop"); Id(_topFirst, "ExTopFirst");
            Id(_ok, "ExOk"); Id(_cancel, "ExCancel"); Id(_status, "ExStatus"); Id(_browse, "ExBrowse");

            StackPanel root = new StackPanel { Margin = new Thickness(16) };
            _inputs = root;
            root.Children.Add(Row(Label(LangResources.Get(TextKey.Common_OutputFile)), _path, _browse));
            root.Children.Add(Hint(mode == ExtraExportMode.Gif
                ? LangResources.Get(TextKey.ExportExtra_FilesWithTheSameName)
                : LangResources.Get(TextKey.ExportExtra_TheImageUsesThisFile)));

            if (mode == ExtraExportMode.Gif)
            {
                root.Children.Add(Title2(LangResources.Get(TextKey.Common_Angle)));
                root.Children.Add(Row(Label(LangResources.Get(TextKey.Common_Yaw)), _yaw, Label(LangResources.Get(TextKey.Common_DegPitch)), _elev, Label(LangResources.Get(TextKey.Common_Deg))));
                root.Children.Add(Hint(LangResources.Get(TextKey.Common_DefaultsToTheCurrentPreview)));
            }

            root.Children.Add(Title2(LangResources.Get(TextKey.Common_Image)));
            root.Children.Add(Row(Label(LangResources.Get(TextKey.Common_IntegerScale)), _scale, Label(LangResources.Get(TextKey.Common_HowManyPixelsEachCell))));
            root.Children.Add(Row(Label(LangResources.Get(TextKey.Common_Padding)), _padding, Label(LangResources.Get(TextKey.Common_ExtraPixelsKeptOnEach))));
            if (mode == ExtraExportMode.Gif)
            {
                root.Children.Add(Row(Label(LangResources.Get(TextKey.ExportExtra_Background)), _bg, Label(LangResources.Get(TextKey.ExportExtra_GIFTransparencyIsOnlyOn))));
                root.Children.Add(_shade);
                if (outline != null)
                {
                    root.Children.Add(_outline);
                    root.Children.Add(Hint(LangResources.Get(TextKey.Common_ExportOutlineHint)));
                }
            }
            root.Children.Add(_trim);
            if (mode == ExtraExportMode.Gif) { root.Children.Add(_loop); }
            else
            {
                root.Children.Add(_topFirst);
                root.Children.Add(Hint(LangResources.Get(TextKey.ExportExtra_LookingStraightDownEachSlice)));
            }

            root.Children.Add(_summary);
            root.Children.Add(_progress);
            root.Children.Add(_status);

            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            buttons.Children.Add(_ok);
            buttons.Children.Add(_cancel);
            root.Children.Add(buttons);
            Content = root;

            _browse.Click += Browse_Click;
            _ok.Click += Ok_Click;
            _cancel.Click += (s, e) => { if (_running) { _cancelRequested = true; _status.Text = LangResources.Get(TextKey.Common_Cancelling); } };
            Closing += (s, e) => { if (_running) { _cancelRequested = true; e.Cancel = true; _status.Text = LangResources.Get(TextKey.Common_Cancelling); } };
            _scale.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((s, e) => UpdateSummary()));
            _scale.SelectionChanged += (s, e) => UpdateSummary();
            _trim.Checked += (s, e) => UpdateSummary(); _trim.Unchecked += (s, e) => UpdateSummary();
            Loaded += (s, e) => UpdateSummary();
        }

        private static void Id(DependencyObject o, string id) { AutomationProperties.SetAutomationId(o, id); }

        private void UpdateSummary()
        {
            int scale;
            if (!int.TryParse(_scale.Text.Trim(), out scale) || scale < 1 || scale > 16) { _summary.Text = ""; return; }

            if (_mode == ExtraExportMode.Gif)
            {
                int diag = (int)Math.Ceiling(Math.Sqrt((double)_anim.Width * _anim.Width + (double)_anim.Height * _anim.Height));
                _summary.Text = LangResources.Format(TextKey.ExportGif_Summary, _anim.Count, _anim.Fps, (diag + 2) * scale, (diag + _anim.Depth + 2) * scale, (_trim.IsChecked == true ? LangResources.Get(TextKey.Common_UsuallyMuchSmallerAfterTrimming) : LangResources.Get(TextKey.Common_NotTrimmed)));
            }
            else
            {
                long w = (long)_anim.Width * scale * _anim.Depth, h = (long)_anim.Height * scale * _anim.Count;
                _summary.Text = LangResources.Format(TextKey.ExportStrip_Summary, _anim.Depth, _anim.Count, _anim.Width * scale, _anim.Height * scale, w, h, SpriteExporter.MaxTextureSide);
            }
        }

        // --- 讀取輸入 --------------------------------------------------------------------------------------
        private bool TryReadCommon(out int scale, out int padding, out string path, out string error)
        {
            scale = padding = 0;
            path = null;
            error = null;
            if (string.IsNullOrWhiteSpace(_path.Text)) { error = LangResources.Get(TextKey.Common_PleaseChooseAnOutputFile); return false; }
            if (!int.TryParse(_scale.Text.Trim(), out scale) || scale < 1 || scale > 16) { error = LangResources.Get(TextKey.Export_ScaleInvalid); return false; }
            if (!int.TryParse(_padding.Text.Trim(), out padding) || padding < 0 || padding > 64) { error = LangResources.Get(TextKey.Export_PaddingInvalid); return false; }

            path = _path.Text.Trim();
            string ext = _mode == ExtraExportMode.Gif ? ".gif" : ".png";
            if (!path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { path += ext; }
            return true;
        }

        private static bool TryDouble(string text, out double v)
        {
            return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !double.IsInfinity(v) && !double.IsNaN(v);
        }

        public bool TryReadGif(out GifOptions options, out string error)
        {
            options = null;
            int scale, padding; string path;
            if (!TryReadCommon(out scale, out padding, out path, out error)) { return false; }

            double yaw, elev;
            if (!TryDouble(_yaw.Text, out yaw)) { error = LangResources.Get(TextKey.Common_PleaseEnterANumberFor); return false; }
            if (!TryDouble(_elev.Text, out elev) || elev < -90 || elev > 90) { error = LangResources.Get(TextKey.Common_PleaseEnterANumberFrom); return false; }

            int bgIndex = Math.Max(0, _bg.SelectedIndex);
            options = new GifOptions
            {
                Path = path, YawDegrees = yaw, ElevationDegrees = elev, Scale = scale, Padding = padding,
                Shade = _shade.IsChecked == true, Trim = _trim.IsChecked == true, Loop = _loop.IsChecked == true,
                Outline = _outline.IsChecked == true && _outlineSettings != null ? _outlineSettings.Clone() : null,
                TransparentBackground = bgIndex == 0, Background = BgColours[bgIndex],
            };
            return true;
        }

        public bool TryReadStrip(out StripOptions options, out string error)
        {
            options = null;
            int scale, padding; string path;
            if (!TryReadCommon(out scale, out padding, out path, out error)) { return false; }

            options = new StripOptions { Path = path, Scale = scale, Padding = padding, Trim = _trim.IsChecked == true, TopFirst = _topFirst.IsChecked == true };
            return true;
        }

        // --- 動作 ------------------------------------------------------------------------------------------
        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            string ext = _mode == ExtraExportMode.Gif ? "gif" : "png";
            SaveFileDialog dlg = new SaveFileDialog { Filter = $"{ext.ToUpperInvariant()} (*.{ext})|*.{ext}", DefaultExt = "." + ext, FileName = Path.GetFileName(_path.Text) };
            try
            {
                string dir = Path.GetDirectoryName(_path.Text);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) { dlg.InitialDirectory = dir; }
            }
            catch (ArgumentException) { }

            if (dlg.ShowDialog(this) == true) { _path.Text = dlg.FileName; }
        }

        private async void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (_running) { return; }

            GifOptions gif = null;
            StripOptions strip = null;
            string error;
            bool ok = _mode == ExtraExportMode.Gif ? TryReadGif(out gif, out error) : TryReadStrip(out strip, out error);
            if (!ok)
            {
                _status.Foreground = Brushes.OrangeRed;
                _status.Text = error;
                return;
            }

            _running = true;
            _cancelRequested = false;
            SetInputsEnabled(false);
            _cancel.Content = LangResources.Get(TextKey.Common_Cancel);
            _status.Foreground = Foreground;
            _status.Text = LangResources.Get(TextKey.Common_Exporting);
            _progress.Value = 0;

            try
            {
                Action<int, int> progress = (done, total) => Dispatcher.BeginInvoke(new Action(() => { _progress.Maximum = total; _progress.Value = done; }));
                Func<bool> cancelled = () => _cancelRequested;
                if (_mode == ExtraExportMode.Gif)
                {
                    GifResult r = await Task.Run(() => GifExporter.Export(_anim, gif, progress, cancelled));
                    LastPath = r.Path;
                    LastDescription = r.Describe();
                }
                else
                {
                    StripResult r = await Task.Run(() => StripExporter.Export(_anim, strip, progress, cancelled));
                    LastPath = r.PngPath;
                    LastDescription = r.Describe();
                }
                _status.Text = LangResources.Format(TextKey.Export_ResultDone, LastDescription, LastPath);
            }
            catch (OperationCanceledException)
            {
                _status.Text = LangResources.Get(TextKey.Common_Cancelled);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is IOException || ex is UnauthorizedAccessException)
            {
                _status.Foreground = Brushes.OrangeRed;
                _status.Text = LangResources.Format(TextKey.Export_ResultFailed, ex.Message);
            }
            finally
            {
                _running = false;
                SetInputsEnabled(true);
                _cancel.Content = LangResources.Get(TextKey.Tip_Close);
            }
        }

        private void SetInputsEnabled(bool on)
        {
            UIElement buttonRow = (UIElement)_cancel.Parent;
            foreach (UIElement c in _inputs.Children)
            {
                if (!ReferenceEquals(c, _progress) && !ReferenceEquals(c, _status) && !ReferenceEquals(c, _summary) && !ReferenceEquals(c, buttonRow)) { c.IsEnabled = on; }
            }
            _ok.IsEnabled = on;
        }

        // --- 版面小工具 -------------------------------------------------------------------------------------
        private static TextBox Box(double width)
        {
            return new TextBox { Width = width, Margin = new Thickness(6, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center };
        }

        private static TextBlock Label(string text) { return new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }; }

        private static TextBlock Title2(string text)
        {
            return new TextBlock { Text = text, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 12, 0, 4), Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xDC, 0xFE)) };
        }

        private static TextBlock Hint(string text)
        {
            return new TextBlock { Text = text, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)), TextWrapping = TextWrapping.Wrap, MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 0) };
        }

        private static StackPanel Row(params UIElement[] items)
        {
            StackPanel p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
            foreach (UIElement i in items) { p.Children.Add(i); }
            return p;
        }
    }
}
