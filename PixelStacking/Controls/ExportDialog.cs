using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using PixelStacking.Data;
using PixelStacking.Render;

namespace PixelStacking.Controls
{
    /// <summary>
    /// 「匯出 PNG」對話框：固定角度（可一次輸出繞一圈的 N 個方向）× 全部幀，輸出圖集＋JSON 和／或 PNG 序列。
    /// 匯出在背景執行緒進行（有進度條、可取消），期間不能改動動畫。
    /// </summary>
    public sealed class ExportDialog : Window
    {
        private readonly Animation _anim;
        private readonly TextBox _path = new TextBox { MinWidth = 300 };
        private readonly TextBox _yaw = Box(70), _elev = Box(70), _padding = Box(50);
        private readonly ComboBox _dirs = Combo(60, "1", "2", "4", "8", "16");
        private readonly ComboBox _scale = Combo(60, "1", "2", "3", "4", "6", "8");
        private readonly CheckBox _shade = new CheckBox { Content = LangResources.Get(TextKey.Common_ShadingLightAndDarkBy), Margin = new Thickness(0, 4, 0, 0) };
        private readonly CheckBox _outline = new CheckBox { Content = LangResources.Get(TextKey.Common_ExportOutline), Margin = new Thickness(0, 4, 0, 0) };
        private readonly OutlineOptions _outlineSettings;
        private readonly CheckBox _trim = new CheckBox { Content = LangResources.Get(TextKey.Export_TrimTheCommonEmptyMargin), IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
        private readonly RadioButton _kindBoth = new RadioButton { Content = LangResources.Get(TextKey.Export_Both), IsChecked = true, GroupName = "Kind", Margin = new Thickness(0, 2, 12, 2) };
        private readonly RadioButton _kindSheet = new RadioButton { Content = LangResources.Get(TextKey.Export_AtlasJSON), GroupName = "Kind", Margin = new Thickness(0, 2, 12, 2) };
        private readonly RadioButton _kindSeq = new RadioButton { Content = LangResources.Get(TextKey.Export_PNGSequence), GroupName = "Kind", Margin = new Thickness(0, 2, 12, 2) };
        private readonly TextBlock _summary = new TextBlock { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, MinHeight = 34 };
        private readonly TextBlock _status = new TextBlock { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, MinHeight = 34 };
        private readonly ProgressBar _progress = new ProgressBar { Height = 8, Margin = new Thickness(0, 6, 0, 0), Minimum = 0, Maximum = 1 };
        private readonly Button _ok = new Button { Content = LangResources.Get(TextKey.Common_Export), Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        private readonly Button _cancel = new Button { Content = LangResources.Get(TextKey.Tip_Close), Width = 80, IsCancel = true };
        private readonly Button _browse = new Button { Content = LangResources.Get(TextKey.Common_ChooseLocation), Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 1, 8, 1) };
        private readonly Panel _inputs;

        private bool _running;
        private volatile bool _cancelRequested;

        /// <summary>最近一次匯出成功的結果（沒成功過 = null）。</summary>
        public ExportResult LastResult { get; private set; }

        public ExportDialog(Animation anim, double yawDegrees, double elevationDegrees, bool shade, string suggestedPath)
            : this(anim, yawDegrees, elevationDegrees, shade, suggestedPath, null, false) { }

        /// <param name="outline">輪廓線設定（null = 這個對話框不提供輪廓線）；<paramref name="outlineOn"/> = 「輪廓線」勾選的預設值（預覽有開就勾）。</param>
        public ExportDialog(Animation anim, double yawDegrees, double elevationDegrees, bool shade, string suggestedPath, OutlineOptions outline, bool outlineOn)
        {
            _anim = anim;
            _outlineSettings = outline;
            DialogStyle.Apply(this);
            Title = LangResources.Get(TextKey.Export_ExportPNGFixedAnglesFor);
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
            _dirs.Text = "1";
            _scale.Text = "4";
            _shade.IsChecked = shade;
            _outline.IsChecked = outline != null && outlineOn;

            StackPanel root = new StackPanel { Margin = new Thickness(16) };
            _inputs = root;

            root.Children.Add(Row(Label(LangResources.Get(TextKey.Common_OutputFile)), _path, _browse));
            root.Children.Add(Hint(LangResources.Get(TextKey.Export_TheAtlasUsesThisFile)));

            root.Children.Add(Title2(LangResources.Get(TextKey.Common_Angle)));
            root.Children.Add(Row(Label(LangResources.Get(TextKey.Common_Yaw)), _yaw, Label(LangResources.Get(TextKey.Common_DegPitch)), _elev, Label(LangResources.Get(TextKey.Common_Deg))));
            root.Children.Add(Hint(LangResources.Get(TextKey.Common_DefaultsToTheCurrentPreview)));
            root.Children.Add(Row(Label(LangResources.Get(TextKey.Export_Directions)), _dirs, Label(LangResources.Get(TextKey.Export_HowManyDirectionsAroundA))));

            root.Children.Add(Title2(LangResources.Get(TextKey.Common_Image)));
            root.Children.Add(Row(Label(LangResources.Get(TextKey.Common_IntegerScale)), _scale, Label(LangResources.Get(TextKey.Common_HowManyPixelsEachCell))));
            root.Children.Add(Row(Label(LangResources.Get(TextKey.Common_Padding)), _padding, Label(LangResources.Get(TextKey.Common_ExtraPixelsKeptOnEach))));
            root.Children.Add(_shade);
            if (outline != null)
            {
                root.Children.Add(_outline);
                root.Children.Add(Hint(LangResources.Get(TextKey.Common_ExportOutlineHint)));
            }
            root.Children.Add(_trim);

            root.Children.Add(Title2(LangResources.Get(TextKey.Export_Output)));
            root.Children.Add(Row(_kindBoth, _kindSheet, _kindSeq));
            root.Children.Add(Hint(LangResources.Get(TextKey.Export_AtlasOneDirectionPerRow)));

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
            Closing += (s, e) =>
            {
                if (_running) { _cancelRequested = true; e.Cancel = true; _status.Text = LangResources.Get(TextKey.Common_Cancelling); }
            };
            RoutedEventHandler update = (s, e) => UpdateSummary();
            _dirs.AddHandler(TextBoxBase_TextChanged, new TextChangedEventHandler((s, e) => UpdateSummary()));
            _scale.AddHandler(TextBoxBase_TextChanged, new TextChangedEventHandler((s, e) => UpdateSummary()));
            _dirs.SelectionChanged += (s, e) => UpdateSummary();
            _scale.SelectionChanged += (s, e) => UpdateSummary();
            _trim.Checked += update; _trim.Unchecked += update;
            Loaded += (s, e) => UpdateSummary();
        }

        private static readonly RoutedEvent TextBoxBase_TextChanged = System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent;

        // --- 讀取輸入 --------------------------------------------------------------------------------------
        /// <summary>讀取並驗證輸入；不合法時回傳 false 並給出原因。</summary>
        public bool TryReadOptions(out ExportOptions options, out string error)
        {
            options = null;
            error = null;

            double yaw, elev;
            int dirs, scale, padding;
            if (string.IsNullOrWhiteSpace(_path.Text)) { error = LangResources.Get(TextKey.Common_PleaseChooseAnOutputFile); return false; }
            if (!TryDouble(_yaw.Text, out yaw)) { error = LangResources.Get(TextKey.Common_PleaseEnterANumberFor); return false; }
            if (!TryDouble(_elev.Text, out elev) || elev < -90 || elev > 90) { error = LangResources.Get(TextKey.Common_PleaseEnterANumberFrom); return false; }
            if (!int.TryParse(_dirs.Text.Trim(), out dirs) || dirs < 1 || dirs > 64) { error = LangResources.Get(TextKey.Export_PleaseEnterAWholeNumber); return false; }
            if (!int.TryParse(_scale.Text.Trim(), out scale) || scale < 1 || scale > 16) { error = LangResources.Get(TextKey.Export_ScaleInvalid); return false; }
            if (!int.TryParse(_padding.Text.Trim(), out padding) || padding < 0 || padding > 64) { error = LangResources.Get(TextKey.Export_PaddingInvalid); return false; }

            string path = _path.Text.Trim();
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) { path += ".png"; }

            options = new ExportOptions
            {
                Path = path,
                YawDegrees = yaw,
                ElevationDegrees = elev,
                Directions = dirs,
                Scale = scale,
                Padding = padding,
                Shade = _shade.IsChecked == true,
                Outline = _outline.IsChecked == true && _outlineSettings != null ? _outlineSettings.Clone() : null,
                Trim = _trim.IsChecked == true,
                Kind = _kindSheet.IsChecked == true ? ExportKind.Sheet : _kindSeq.IsChecked == true ? ExportKind.Sequence : ExportKind.Both,
            };
            return true;
        }

        private static bool TryDouble(string text, out double v)
        {
            return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !double.IsInfinity(v) && !double.IsNaN(v);
        }

        private void UpdateSummary()
        {
            int dirs, scale;
            if (!int.TryParse(_dirs.Text.Trim(), out dirs) || dirs < 1 || dirs > 64
                || !int.TryParse(_scale.Text.Trim(), out scale) || scale < 1 || scale > 16)
            {
                _summary.Text = "";
                return;
            }

            // 未裁切時每格大小 = 渲染器畫面（最大值，裁切後只會更小）
            int diag = (int)Math.Ceiling(Math.Sqrt((double)_anim.Width * _anim.Width + (double)_anim.Height * _anim.Height));
            int maxW = (diag + 2) * scale, maxH = (diag + _anim.Depth + 2) * scale;
            _summary.Text = LangResources.Format(TextKey.Export_CellsSummary, dirs, _anim.Count, dirs * _anim.Count)
                + LangResources.Format(TextKey.Export_LargestCell, maxW, maxH, (_trim.IsChecked == true ? LangResources.Get(TextKey.Common_UsuallyMuchSmallerAfterTrimming) : LangResources.Get(TextKey.Common_NotTrimmed)));
        }

        // --- 動作 ------------------------------------------------------------------------------------------
        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            SaveFileDialog dlg = new SaveFileDialog { Filter = "PNG (*.png)|*.png", DefaultExt = ".png", FileName = Path.GetFileName(_path.Text) };
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

            ExportOptions options;
            string error;
            if (!TryReadOptions(out options, out error))
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
                ExportResult result = await Task.Run(() => SpriteExporter.Export(
                    _anim, options,
                    (done, total) => Dispatcher.BeginInvoke(new Action(() => { _progress.Maximum = total; _progress.Value = done; })),
                    () => _cancelRequested));

                LastResult = result;
                _status.Text = LangResources.Format(TextKey.Export_ResultDone, result.Describe(), (result.SheetPath ?? result.SequenceDir));
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
            UIElement buttonRow = (UIElement)_cancel.Parent;   // 匯出中要能按取消，所以按鈕列不整個停用
            foreach (UIElement c in _inputs.Children)
            {
                if (!ReferenceEquals(c, _progress) && !ReferenceEquals(c, _status) && !ReferenceEquals(c, _summary) && !ReferenceEquals(c, buttonRow))
                {
                    c.IsEnabled = on;
                }
            }
            _ok.IsEnabled = on;
        }

        // --- 版面小工具 -------------------------------------------------------------------------------------
        private static TextBox Box(double width)
        {
            return new TextBox { Width = width, Margin = new Thickness(6, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center };
        }

        private static ComboBox Combo(double width, params string[] items)
        {
            ComboBox c = new ComboBox { Width = width, IsEditable = true, Margin = new Thickness(6, 0, 6, 0) };
            foreach (string s in items) { c.Items.Add(s); }
            return c;
        }

        private static TextBlock Label(string text)
        {
            return new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        }

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
