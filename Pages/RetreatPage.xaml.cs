using System;
using System.IO;
using System.Linq;
using System.Threading;
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
    /// 执行前进行权限/占用/空间检查，占用可强制结束后复检。
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
            var path = PathHistoryHelper.Browse("选择迁移后的链接路径", LinkBox, LinkKey);
            if (path != null) { LinkBox.Text = path; PathHistoryHelper.Remember(LinkBox, LinkKey); }
            ResolveTargetDisplay();
        }

        /// <summary>算可用空间：显示链接路径所在磁盘的可用空间（退迁时内容将复制回此处）</summary>
        private void CalcLinkSpace_Click(object sender, RoutedEventArgs e)
        {
            var link = LinkBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(link))
            {
                DialogHelper.Warn("提示", "请先填写链接路径。");
                return;
            }
            var free = SizeHelper.GetFreeSpace(link);
            LinkSpaceText.Text = free == null ? "无法获取可用空间" : $"可用 {SizeHelper.Format(free.Value)}";
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
            if (!Directory.Exists(target)) { DialogHelper.Error("错误", $"链接目标不存在：\n{target}"); return; }

            PathHistoryHelper.Remember(LinkBox, LinkKey);

            RetreatButton.IsEnabled = false;
            RetreatButton.Content = "检测中...";
            BusyState.Begin();
            try
            {
                // 前置检查：目标内容权限 → 占用闭环 → 空间（复制回链接所在盘，含免责确认）
                var scan = await Task.Run(() => FileLockHelper.ScanDirectory(target));
                if (scan.Denied.Count > 0)
                {
                    var shown = string.Join("\n", scan.Denied.Take(20));
                    if (scan.Denied.Count > 20) shown += $"\n... 共 {scan.Denied.Count} 项";
                    DialogHelper.Error("权限不足", $"链接目标内以下文件无读取权限，无法退迁：\n\n{shown}\n\n请尝试以管理员身份运行或检查文件权限。");
                    return;
                }

                if (scan.Locked.Count > 0)
                {
                    var resolved = await PreCheckHelper.ResolveLocksAsync("文件被占用",
                        $"有程序正在占用链接目标内容，需要退出或强制结束相关程序后才能继续：\n\n{target}",
                        () => PreCheckHelper.DetectLockProcesses(target));
                    if (!resolved) return;
                }

                if (!PreCheckHelper.CheckSpaceInteractive(scan.TotalBytes, link,
                        $"退迁将把内容复制回链接路径所在磁盘：\n{link}\n（内容大小 {SizeHelper.Format(scan.TotalBytes)}）"))
                    return;

                if (!DialogHelper.Confirm("确认退迁",
                    $"将退迁该目录链接：\n\n链接：{link}\n  → 指向 →\n{target}\n\n执行后：把目标内容移回链接路径，删除链接与旧目标。\n\n是否继续？",
                    "退迁", danger: true))
                    return;

                RetreatButton.Content = "退迁中...";
                ResetProgress();
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
                BusyState.End();
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
