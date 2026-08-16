using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace linktool.Dialogs
{
    /// <summary>消息弹窗类型</summary>
    public enum DialogKind { Info, Warning, Error }

    /// <summary>
    /// 现代化信息/警告/错误弹窗（单按钮）。
    /// </summary>
    public partial class MessageDialog : Window
    {
        public MessageDialog(string title, string message, DialogKind kind = DialogKind.Info)
        {
            InitializeComponent();
            TitleText.Text = title;
            MessageText.Text = message;

            Brush accent = Brushes.Transparent;
            switch (kind)
            {
                case DialogKind.Info:
                    accent = (Brush)FindResource("AccentBrush");
                    IconGlyph.Text = "\uE946"; // Info
                    break;
                case DialogKind.Warning:
                    accent = (Brush)FindResource("WarnBrush");
                    IconGlyph.Text = "\uE7BA"; // Warning
                    break;
                case DialogKind.Error:
                    accent = (Brush)FindResource("DangerBrush");
                    IconGlyph.Text = "\uE783"; // Error
                    break;
            }
            IconBadge.Background = accent;

            var main = Application.Current.MainWindow;
            if (main != null && main.IsVisible)
                Owner = main;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape || e.Key == Key.Enter) DialogResult = true;
        }
    }
}