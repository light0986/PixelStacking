using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using PixelStacking.Render;
using PixelStacking.Data;

namespace PixelStacking.Controls
{
    /// <summary>
    /// 「匯入圖片」對話框：選好檔案之後問怎麼放：每個檔案一層，或把一張橫向條帶（疊層條帶）切成多層；
    /// 尺寸跟畫布不一樣時怎麼對齊；透明的地方要不要保留原本的內容。
    /// </summary>
    public sealed class ImportImageDialog : Window
    {
        private readonly RadioButton _modeFiles = new RadioButton { GroupName = "ImpMode", IsChecked = true, Margin = new Thickness(0, 2, 0, 2) };
        private readonly RadioButton _modeStrip = new RadioButton { GroupName = "ImpMode", Content = LangResources.Get(TextKey.ImportImage_CutThisOneImageHorizontally), Margin = new Thickness(0, 2, 0, 2) };
        private readonly TextBox _layerWidth = new TextBox { Width = 60, Margin = new Thickness(6, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center };
        private readonly ComboBox _fit = new ComboBox { Width = 170, Margin = new Thickness(6, 0, 6, 0) };
        private readonly CheckBox _keep = new CheckBox { Content = LangResources.Get(TextKey.ImportImage_OnlyOverwriteCellsThatHave), Margin = new Thickness(0, 6, 0, 0) };
        private readonly TextBlock _error = new TextBlock { Foreground = Brushes.OrangeRed, Margin = new Thickness(0, 6, 0, 0), MinHeight = 18 };

        public bool Strip { get; private set; }
        public int LayerWidth { get; private set; }
        public ImageFit Fit { get; private set; }
        public bool KeepTransparent { get; private set; }

        /// <param name="fileInfo">每個檔案一行的說明（檔名與尺寸）。</param>
        /// <param name="canvasW">目前畫布的寬、高。</param>
        /// <param name="startLabel">從哪一層開始放（給使用者看的文字，例如「第 17 層」）。</param>
        /// <param name="unit">「層」或「切片」。</param>
        /// <param name="stripCandidateWidth">單張圖時預設的每層寬度（通常是畫布寬）。</param>
        public ImportImageDialog(string[] fileInfo, int canvasW, int canvasH, string startLabel, string unit, int stripCandidateWidth)
        {
            DialogStyle.Apply(this);
            Title = LangResources.Get(TextKey.Common_ImportImage);
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            MinWidth = 420;
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
            Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));

            bool single = fileInfo.Length == 1;
            _modeFiles.Content = single ? LangResources.Format(TextKey.ImportImage_OptionOneUnit, unit, unit) : LangResources.Format(TextKey.ImportImage_OptionOneUnitPerFile, unit, startLabel);
            _layerWidth.Text = stripCandidateWidth.ToString(CultureInfo.InvariantCulture);
            _fit.Items.Add(LangResources.Get(TextKey.ImportImage_CenteredAnythingOutsideIsCropped));
            _fit.Items.Add(LangResources.Get(TextKey.Common_AlignTopLeft));
            _fit.Items.Add(LangResources.Get(TextKey.ImportImage_ScaleToExactlyTheCanvas));
            _fit.SelectedIndex = 0;
            _fit.Width = 260;

            AutomationProperties.SetAutomationId(_modeFiles, "ImpModeFiles");
            AutomationProperties.SetAutomationId(_modeStrip, "ImpModeStrip");
            AutomationProperties.SetAutomationId(_layerWidth, "ImpLayerW");
            AutomationProperties.SetAutomationId(_fit, "ImpFit");
            AutomationProperties.SetAutomationId(_keep, "ImpKeep");
            AutomationProperties.SetAutomationId(_error, "ImpError");

            StackPanel root = new StackPanel { Margin = new Thickness(16) };
            root.Children.Add(new TextBlock { Text = LangResources.Format(TextKey.ImportImage_Header, fileInfo.Length, canvasW, canvasH) });
            root.Children.Add(new TextBlock { Text = string.Join("\n", fileInfo), FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)), Margin = new Thickness(8, 4, 0, 6), MaxWidth = 460, TextWrapping = TextWrapping.Wrap });

            root.Children.Add(Title2(LangResources.Get(TextKey.ImportImage_Placement)));
            root.Children.Add(_modeFiles);
            if (single)
            {
                root.Children.Add(_modeStrip);
                StackPanel row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(22, 2, 0, 0) };
                row.Children.Add(new TextBlock { Text = LangResources.Format(TextKey.ImportImage_WidthPerUnit, unit), VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(_layerWidth);
                row.Children.Add(new TextBlock { Text = LangResources.Format(TextKey.ImportImage_PixelsStartingAt, startLabel), VerticalAlignment = VerticalAlignment.Center });
                root.Children.Add(row);
                root.Children.Add(Hint(LangResources.Get(TextKey.ImportImage_TheStripUsesOnlyThe)));
            }

            root.Children.Add(Title2(LangResources.Get(TextKey.ImportImage_WhenTheSizesDiffer)));
            root.Children.Add(_fit);
            root.Children.Add(_keep);
            root.Children.Add(Hint(LangResources.Get(TextKey.ImportImage_ColoursAreMappedToThe)));
            root.Children.Add(_error);

            Button ok = new Button { Content = LangResources.Get(TextKey.ImportImage_Import), Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            Button cancel = new Button { Content = LangResources.Get(TextKey.Common_Cancel), Width = 80, IsCancel = true };
            AutomationProperties.SetAutomationId(ok, "ImpOk");
            AutomationProperties.SetAutomationId(cancel, "ImpCancel");
            ok.Click += Ok_Click;
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);
            Content = root;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            int w = 0;
            bool strip = _modeStrip.IsChecked == true;
            if (strip && (!int.TryParse(_layerWidth.Text.Trim(), out w) || w < 1 || w > 4096))
            {
                _error.Text = LangResources.Get(TextKey.ImportImage_PleaseEnterAWholeNumber);
                return;
            }

            Strip = strip;
            LayerWidth = w;
            Fit = _fit.SelectedIndex == 1 ? ImageFit.TopLeft : _fit.SelectedIndex == 2 ? ImageFit.Stretch : ImageFit.Center;
            KeepTransparent = _keep.IsChecked == true;
            DialogResult = true;
        }

        private static TextBlock Title2(string text)
        {
            return new TextBlock { Text = text, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 8, 0, 4), Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xDC, 0xFE)) };
        }

        private static TextBlock Hint(string text)
        {
            return new TextBlock { Text = text, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)), TextWrapping = TextWrapping.Wrap, MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 0) };
        }
    }
}
