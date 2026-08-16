using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using linktool.Services;

namespace linktool.Pages
{
    /// <summary>
    /// 退迁页：把单个迁移生成的 Junction 目录内容移回原路径并删除链接。
    /// </summary>
    public partial class RetreatPage : UserControl
    {
        private const string LinkKey = "retreat.link";

        public RetreatPage()
        {
            InitializeComponent();
            PathHistoryHelper.Load(LinkBox, LinkKey);
        }

        private void Path_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                PathHistoryHelper.Remember(LinkBox, LinkKey);
        }

        private void LinkBox_KeyUp(object sender, KeyEventArgs e)
            => ResolveTargetDisplay();

        private void LinkBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var path = PathHistoryHelper.Browse("选择迁移后的链接路径");
            if (path != null) { LinkBox.Text = path; PathHistoryHelper.Remember(LinkBox, LinkKey); }
            ResolveTargetDisplay();
        }

        /// <summary>根据输入的链接路径解析目标并显示（失败则显示提示，不阻断）</summary>
        private void ResolveTargetDisplay()
        {
            var link = LinkBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(link) || !Directory.Exists(link) || !JunctionHelper.IsReparsePoint(link))
            {
                TargetText.Text = "（输入链接路径后自动解析）";
                return;
            }
            try
            {
                TargetText.Text = RetreatHelper.ResolveTarget(link);
            }
            catch
            {
                TargetText.Text = "（无法解析链接目标）";
            }
        }

        private async void RetreatButton_Click(object sender, RoutedEventArgs e)
        {
            var link = LinkBox.Text?.Trim() ?? "";
            StatusText.Visibility = Visibility.Collapsed;

            if (string.IsNullOrWhiteSpace(link)) { DialogHelper.Warn("提示", "请输入或选择链接路径。"); return; }
            if (!Directory.Exists(link)) { DialogHelper.Error("错误", $"链接路径不存在：\n{link}"); return; }
            if (!JunctionHelper.IsReparsePoint(link)) { DialogHelper.Warn("提示", $"该路径不是目录链接，无需退迁：\n{link}"); return; }

            string target;
            try { target = RetreatHelper.ResolveTarget(link); }
            catch (Exception ex) { DialogHelper.Error("错误", $"解析链接目标失败：\n{ex.Message}"); return; }

            PathHistoryHelper.Remember(LinkBox, LinkKey);

            if (!DialogHelper.Confirm("确认退迁",
                $"将退迁该目录链接：\n\n链接：{link}\n  → 指向 →\n{target}\n\n执行后：把目标内容移回链接路径，删除链接与旧目标。\n\n是否继续？",
                "退迁", danger: true))
                return;

            RetreatButton.IsEnabled = false;
            RetreatButton.Content = "退迁中...";
            ResetProgress();
            try
            {
                await Task.Run(() => RetreatHelper.Retreat(link,
                    (c, t, f) => Dispatcher.Invoke(() => SetProgress(c, t, f))));
                ShowStatus("退迁完成。", true);
            }
            catch (Exception ex)
            {
                ShowStatus($"退迁失败：{ex.Message}", false);
            }
            finally
            {
                RetreatButton.IsEnabled = true;
                RetreatButton.Content = "退迁";
                ResetProgress();
            }
        }

        private void SetProgress(int completed, int total, string current)
        {
            ProgressCountText.Text = $"{completed}/{total}";
            ProgressBar.Maximum = Math.Max(1, total);
            ProgressBar.Value = completed;
            CurrentFileText.Text = current;
        }

        private void ResetProgress()
        {
            ProgressCountText.Text = "0/0";
            ProgressBar.Value = 0;
            CurrentFileText.Text = "";
        }

        private void ShowStatus(string text, bool success)
        {
            StatusText.Text = text;
            StatusText.Foreground = success
                ? (Brush)FindResource("SuccessBrush")
                : (Brush)FindResource("DangerBrush");
            StatusText.Visibility = Visibility.Visible;
        }
    }
}