using System.Windows;

namespace PixelStacking.Controls
{
    /// <summary>「說明 > 關於」視窗。版面與文字都在 AboutDialog.xaml。</summary>
    public partial class AboutDialog : Window
    {
        public AboutDialog()
        {
            InitializeComponent();
            DialogStyle.AttachCommands(this);   // 自製標題列的關閉鈕需要這個綁定（樣式已在 XAML 套用）
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
