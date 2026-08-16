using System.Windows;
using System.Windows.Input;

namespace linktool.Dialogs
{
    /// <summary>
    /// 现代化精简确认弹窗。返回值：ShowDialog() == true 表示确认。
    /// danger=true 时确认按钮为红色。
    /// </summary>
    public partial class ConfirmDialog : Window
    {
        public ConfirmDialog(string title, string message, string confirmText = "确定", bool danger = false)
        {
            InitializeComponent();
            TitleText.Text = title;
            MessageText.Text = message;
            ConfirmButton.Content = confirmText;
            if (danger)
                ConfirmButton.Style = (Style)FindResource("DangerPrimaryButton");
            // 仅当主窗口已显示时才设为 Owner，避免“Owner 未显示”异常
            var main = Application.Current.MainWindow;
            if (main != null && main.IsVisible)
                Owner = main;
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;

        private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) DialogResult = false;
        }
    }
}