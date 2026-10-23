using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using linktool.Services;

namespace linktool.Dialogs
{
    /// <summary>
    /// 占用进程处理弹窗：列出占用进程供勾选并强制结束。
    /// DialogResult == true 表示用户点击了“强制结束”（外层应复检），
    /// false/关闭 表示放弃操作。
    /// </summary>
    public partial class ProcessListDialog : Window
    {
        private readonly ObservableCollection<ProcItem> _items = new();

        public ProcessListDialog(string title, string message, IReadOnlyList<Process> processes)
        {
            InitializeComponent();
            TitleText.Text = title;
            MessageText.Text = message;
            foreach (var p in processes)
                _items.Add(new ProcItem(p, $"{p.ProcessName} (PID: {p.Id})"));
            ProcList.ItemsSource = _items;

            var main = Application.Current.MainWindow;
            if (main != null && main.IsVisible)
                Owner = main;
        }

        /// <summary>强制结束勾选的进程；结束后关闭弹窗（DialogResult=true）交由外层复检</summary>
        private async void KillButton_Click(object sender, RoutedEventArgs e)
        {
            var targets = _items.Where(i => i.IsChecked).Select(i => i.Process).ToList();
            if (targets.Count == 0)
            {
                DialogHelper.Warn("提示", "请先勾选要结束的进程。");
                return;
            }

            var list = string.Join("\n", targets.Select(p => $"  - {p.ProcessName} (PID: {p.Id})"));
            if (!DialogHelper.Confirm("确认强制结束",
                    $"将强制结束以下 {targets.Count} 个进程，未保存的数据可能丢失：\n\n{list}",
                    "强制结束", danger: true))
                return;

            KillButton.IsEnabled = false;
            KillButton.Content = "结束中...";
            try
            {
                await Task.Run(() =>
                {
                    foreach (var p in targets)
                    {
                        try { p.Kill(); p.WaitForExit(5000); }
                        catch { /* 进程已退出或无权限，忽略 */ }
                    }
                });
            }
            finally
            {
                KillButton.IsEnabled = true;
                KillButton.Content = "强制结束选中进程";
            }
            DialogResult = true; // 交由外层复检
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _items)
                item.IsChecked = true;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) DialogResult = false;
        }

        /// <summary>进程列表项</summary>
        public sealed class ProcItem : INotifyPropertyChanged
        {
            private bool _isChecked = true;

            public Process Process { get; }
            public string Display { get; }

            /// <summary>是否勾选（默认全选）</summary>
            public bool IsChecked
            {
                get => _isChecked;
                set { _isChecked = value; OnPropertyChanged(); }
            }

            public ProcItem(Process process, string display)
            {
                Process = process;
                Display = display;
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            private void OnPropertyChanged([CallerMemberName] string? name = null)
                => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
