using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using linktool.Services;

namespace linktool
{
    /// <summary>
    /// 主窗口：侧边栏导航 + 多页面容器 + 底部管理员警告条。
    /// 页面按需懒加载并缓存于字典。
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly Dictionary<string, UserControl> _pages = new();

        public MainWindow()
        {
            InitializeComponent();
            LoadIcon();
            UpdateAdminState();
            // XAML 加载期 PageHost 尚未创建，无法在此触发导航；须构造完成后显式加载初始页
            NavMigration.IsChecked = true;
        }

        /// <summary>从当前 exe 加载窗口图标</summary>
        private void LoadIcon()
        {
            try
            {
                var exePath = Environment.ProcessPath;
                if (exePath == null) return;
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (icon != null)
                    Icon = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            catch { /* 图标加载失败不影响启动 */ }
        }

        /// <summary>刷新管理员状态：是否显示警告条、侧边栏状态文本、按钮可用性</summary>
        private void UpdateAdminState()
        {
            var isAdmin = AdminHelper.IsRunningAsAdmin();
            AdminWarningBar.Visibility = isAdmin ? Visibility.Collapsed : Visibility.Visible;

            if (isAdmin)
            {
                AdminStatusText.Text = "管理员权限：已获取";
                AdminRestartButton.Visibility = Visibility.Collapsed;
                AdminRestartButton.IsEnabled = false;
            }
            else
            {
                AdminStatusText.Text = "管理员权限：未获取";
                AdminRestartButton.Visibility = Visibility.Visible;
                AdminRestartButton.IsEnabled = true;
            }
        }

        /// <summary>导航选中：懒加载并切换页面</summary>
        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (PageHost is null) return; // XAML 加载期容器尚未创建
            if (!(sender is FrameworkElement fe) || fe.Tag is not string key)
                return;

            if (!_pages.TryGetValue(key, out var page))
            {
                page = key switch
                {
                    "migration" => new Pages.MigrationPage(),
                    "batch-migration" => new Pages.BatchMigrationPage(),
                    "link" => new Pages.LinkPage(),
                    "batch-link" => new Pages.BatchLinkPage(),
                    "retreat" => new Pages.RetreatPage(),
                    "batch-retreat" => new Pages.BatchRetreatPage(),
                    "help" => new Pages.HelpPage(),
                    "about" => new Pages.AboutPage(),
                    _ => null
                };
                if (page != null) _pages[key] = page;
            }

            if (page != null) PageHost.Content = page;
        }

        /// <summary>提权运行按钮</summary>
        private void AdminRestartButton_Click(object sender, RoutedEventArgs e) => AdminHelper.RestartAsAdmin();
    }
}