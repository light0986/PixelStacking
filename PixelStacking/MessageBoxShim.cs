using System.Windows;
using PixelStacking.Controls;

namespace PixelStacking
{
    /// <summary>
    /// 蓋掉 System.Windows.MessageBox（同一個命名空間裡的型別比 using 進來的優先）：訊息與標題先換成目前語言再顯示。
    /// 程式碼用 LangResources.Get／Format 取來的句子直接用，其餘（舊的中文原文、例外訊息）走 Loc.T。
    /// </summary>
    internal static class MessageBox
    {
        public static MessageBoxResult Show(Window owner, string text, string caption, MessageBoxButton button, MessageBoxImage icon)
        {
            return System.Windows.MessageBox.Show(owner, LangResources.Localize(text), LangResources.Localize(caption), button, icon);
        }

        public static MessageBoxResult Show(string text, string caption, MessageBoxButton button, MessageBoxImage icon)
        {
            return System.Windows.MessageBox.Show(LangResources.Localize(text), LangResources.Localize(caption), button, icon);
        }

        public static MessageBoxResult Show(string text)
        {
            return System.Windows.MessageBox.Show(LangResources.Localize(text));
        }
    }
}
