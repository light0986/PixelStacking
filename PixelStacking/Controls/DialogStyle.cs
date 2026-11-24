using System.Windows;
using PixelStacking.Data;
using System.Windows.Input;

namespace PixelStacking.Controls
{
    /// <summary>
    /// 讓對話框套用 App.xaml 裡的 DialogWindowStyle（自製深色標題列 + 關閉鈕，跟主視窗同一套）。
    /// 程式碼建立的對話框在建構子裡呼叫 <see cref="Apply"/>；XAML 對話框寫 Style="{StaticResource DialogWindowStyle}"，
    /// 再在建構子呼叫 <see cref="AttachCommands"/>。
    /// 沒有 Application（例如單元測試、設計工具）或找不到樣式時不套樣式，視窗維持一般外觀。
    /// </summary>
    internal static class DialogStyle
    {
        public const string Key = "DialogWindowStyle";

        /// <summary>套用樣式並接上關閉命令（程式碼建立的對話框用）。</summary>
        public static void Apply(Window window)
        {
            LocHook.Install();
            LocHook.Watch(window);
            Application app = Application.Current;
            if (app != null)
            {
                Style style = app.TryFindResource(Key) as Style;
                if (style != null) { window.Style = style; }
            }
            AttachCommands(window);
        }

        /// <summary>
        /// 標題列的關閉鈕綁的是 SystemCommands.CloseWindowCommand，視窗本身不會處理它（沒有綁定時按鈕會是停用狀態），
        /// 所以每個用這個樣式的視窗都要接上：可以執行、執行時關閉視窗（跟系統標題列的 X 一樣，會走 Closing）。
        /// </summary>
        public static void AttachCommands(Window window)
        {
            window.CommandBindings.Add(new CommandBinding(
                SystemCommands.CloseWindowCommand,
                (s, e) => window.Close(),
                (s, e) => e.CanExecute = true));
        }
    }
}
