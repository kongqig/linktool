using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using linktool.Services;

namespace linktool.Pages
{
    /// <summary>
    /// 链接页：单独创建 Junction 目录链接（链接位置 → 目标路径）。
    /// 执行前进行占用检测，可强制结束后复检。
    /// </summary>
    public partial class LinkPage : UserControl
    {
        private const string LinkKey = "link.location";
        private const string TargetKey = "link.target";

        private CancellationTokenSource? _calcCts;

        public LinkPage()
        {
            InitializeComponent();
            PathHistoryHelper.Load(LinkBox, LinkKey);
            PathHistoryHelper.Load(TargetBox, TargetKey);
        }

        private void Path_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && sender is ComboBox cb)
                PathHistoryHelper.Remember(cb, ReferenceEquals(cb, LinkBox) ? LinkKey : TargetKey);
        }

        private void LinkBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var path = PathHistoryHelper.Browse("选择链接位置", LinkBox, LinkKey);
            if (path != null) { LinkBox.Text = path; PathHistoryHelper.Remember(LinkBox, LinkKey); }
        }

        private void TargetBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var path = PathHistoryHelper.Browse("选择目标文件夹", TargetBox, TargetKey);
            if (path != null) { TargetBox.Text = path; PathHistoryHelper.Remember(TargetBox, TargetKey); }
        }

        /// <summary>算大小：后台遍历统计目标文件夹的总大小与文件数</summary>
        private async void CalcTargetSize_Click(object sender, RoutedEventArgs e)
        {
            var path = TargetBox.Text?.Trim() ?? "";
            if (!Directory.Exists(path))
            {
                DialogHelper.Warn("提示", "请先填写有效的目标路径。");
                return;
            }

            _calcCts?.Cancel();
            using var cts = new CancellationTokenSource();
            _calcCts = cts;
            CalcTargetSizeButton.IsEnabled = false;
            TargetSizeText.Text = "计算中…";
            try
            {
                var result = await SizeHelper.GetSizeAsync(path, cts.Token);
                if (cts.IsCancellationRequested) return;
                TargetSizeText.Text = $"{SizeHelper.Format(result.TotalBytes)} · {result.FileCount} 个文件"
                                      + (result.Incomplete ? "（部分文件无法读取，结果可能偏小）" : "");
            }
            catch (OperationCanceledException) { /* 被新计算取消 */ }
            catch (Exception ex)
            {
                if (!cts.IsCancellationRequested)
                {
                    TargetSizeText.Text = "";
                    DialogHelper.Error("错误", $"计算大小失败：{ex.Message}");
                }
            }
            finally
            {
                if (_calcCts == cts) _calcCts = null;
                CalcTargetSizeButton.IsEnabled = true;
            }
        }

        private async void CreateButton_Click(object sender, RoutedEventArgs e)
        {
            var linkPath = LinkBox.Text?.Trim() ?? "";
            var targetPath = TargetBox.Text?.Trim() ?? "";
            StatusText.Visibility = Visibility.Collapsed;

            if (string.IsNullOrWhiteSpace(linkPath)) { DialogHelper.Warn("提示", "请输入或选择链接位置。"); return; }
            if (string.IsNullOrWhiteSpace(targetPath)) { DialogHelper.Warn("提示", "请输入或选择目标路径。"); return; }
            if (!MoveValidator.IsValidPath(linkPath)) { DialogHelper.Error("错误", $"链接位置格式不合法：\n{linkPath}"); return; }
            if (!MoveValidator.IsValidPath(targetPath)) { DialogHelper.Error("错误", $"目标路径格式不合法：\n{targetPath}"); return; }
            if (!Directory.Exists(targetPath)) { DialogHelper.Error("错误", $"目标路径不存在：\n{targetPath}"); return; }
            if (JunctionHelper.IsReparsePoint(targetPath)) { DialogHelper.Error("错误", $"目标路径已是目录链接：\n{targetPath}"); return; }

            PathHistoryHelper.Remember(LinkBox, LinkKey);
            PathHistoryHelper.Remember(TargetBox, TargetKey);

            CreateButton.IsEnabled = false;
            CreateButton.Content = "检测中...";
            BusyState.Begin();
            try
            {
                // 占用检测：目标路径有进程驻留时提示并可强制结束后复检
                var resolved = await PreCheckHelper.ResolveLocksAsync("目标被占用",
                    "有程序正在目标文件夹内运行或加载文件，创建链接前建议先退出：\n\n" + targetPath,
                    () => FileLockHelper.GetProcessesLockingDirectory(targetPath));
                if (!resolved) return;

                if (!DialogHelper.Confirm("确认创建目录链接",
                    $"将创建目录链接（Junction）：\n\n{linkPath}\n  → 指向 →\n{targetPath}\n\n是否继续？",
                    "创建"))
                    return;

                CreateButton.Content = "创建中...";
                await Task.Run(() => JunctionHelper.CreateJunction(linkPath, targetPath));
                ShowStatus("目录链接创建成功。", true);
            }
            catch (OperationCanceledException) { /* 无取消场景，防御 */ }
            catch (Exception ex)
            {
                ShowStatus($"创建失败：{ex.Message}", false);
            }
            finally
            {
                BusyState.End();
                CreateButton.IsEnabled = true;
                CreateButton.Content = "创建链接";
            }
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
