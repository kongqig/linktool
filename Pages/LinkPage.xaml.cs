using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using linktool.Services;

namespace linktool.Pages
{
    /// <summary>
    /// 链接页：单独创建 Junction 目录链接（链接位置 → 目标路径）。
    /// </summary>
    public partial class LinkPage : UserControl
    {
        private const string LinkKey = "link.location";
        private const string TargetKey = "link.target";

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
            var path = PathHistoryHelper.Browse("选择链接位置");
            if (path != null) { LinkBox.Text = path; PathHistoryHelper.Remember(LinkBox, LinkKey); }
        }

        private void TargetBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var path = PathHistoryHelper.Browse("选择目标文件夹");
            if (path != null) { TargetBox.Text = path; PathHistoryHelper.Remember(TargetBox, TargetKey); }
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

            if (!DialogHelper.Confirm("确认创建目录链接",
                $"将创建目录链接（Junction）：\n\n{linkPath}\n  → 指向 →\n{targetPath}\n\n是否继续？",
                "创建"))
                return;

            CreateButton.IsEnabled = false;
            CreateButton.Content = "创建中...";
            try
            {
                await Task.Run(() => JunctionHelper.CreateJunction(linkPath, targetPath));
                ShowStatus("目录链接创建成功。", true);
            }
            catch (Exception ex)
            {
                ShowStatus($"创建失败：{ex.Message}", false);
            }
            finally
            {
                CreateButton.IsEnabled = true;
                CreateButton.Content = "创建链接";
            }
        }

        private void ShowStatus(string text, bool success)
        {
            StatusText.Text = text;
            StatusText.Foreground = success
                ? (System.Windows.Media.Brush)FindResource("SuccessBrush")
                : (System.Windows.Media.Brush)FindResource("DangerBrush");
            StatusText.Visibility = Visibility.Visible;
        }
    }
}