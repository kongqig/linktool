using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace linktool
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// 主窗口类，负责文件夹选择、管理员权限处理和文件移动操作
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>
        /// 构造函数，初始化窗口组件并检查管理员权限
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();

            // 检查当前是否以管理员身份运行
            CheckAdminPrivilege();
        }

        #region 管理员权限相关

        /// <summary>
        /// 检查当前进程是否拥有管理员权限
        /// 使用WindowsPrincipal判断当前用户是否属于管理员组
        /// </summary>
        /// <returns>true表示当前是管理员权限，false表示不是</returns>
        private static bool IsRunningAsAdmin()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        /// <summary>
        /// 检查管理员权限状态，并更新UI
        /// 如果已经是管理员，禁用"以管理员身份运行"按钮
        /// 如果不是管理员，弹窗提示建议使用管理员权限
        /// </summary>
        private void CheckAdminPrivilege()
        {
            if (IsRunningAsAdmin())
            {
                // 已经是管理员，禁用按钮
                RunAsAdminButton.IsEnabled = false;
                RunAsAdminButton.Content = "已获取管理员权限";
            }
            else
            {
                // 不是管理员，弹窗提示
                MessageBox.Show(
                    "建议使用管理员权限运行，能够避免大部分权限不足的问题。",
                    "提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        /// <summary>
        /// 以管理员身份重新启动当前程序
        /// 使用ProcessStartInfo设置Verb为"runas"来触发UAC提权提示
        /// 当前实例会在新实例启动后退出
        /// </summary>
        private void RestartAsAdmin()
        {
            try
            {
                // 获取当前可执行文件的路径
                var exePath = Environment.ProcessPath;
                if (exePath == null) return;

                // 配置启动信息，请求管理员权限
                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,   // 必须为true才能使用Verb
                    Verb = "runas"             // 请求以管理员身份运行，触发UAC提示
                };

                // 启动新进程
                Process.Start(startInfo);

                // 关闭当前非管理员进程
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                // 用户取消了UAC提示或其他错误
                MessageBox.Show(
                    $"无法以管理员身份运行：{ex.Message}",
                    "错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        #endregion

        #region 文件夹选择相关

        /// <summary>
        /// 打开文件夹选择对话框
        /// 使用Windows内置的OpenFolderDialog（.NET 8+ WPF支持）
        /// </summary>
        /// <param name="description">对话框标题描述</param>
        /// <returns>选中的文件夹路径，如果取消则返回null</returns>
        private string? BrowseFolder(string description)
        {
            // 使用 .NET 8 提供的 OpenFolderDialog
            var dialog = new OpenFolderDialog
            {
                Title = description
            };

            // 显示对话框，用户确认选择
            if (dialog.ShowDialog() == true)
            {
                return dialog.FolderName;
            }

            return null;
        }

        /// <summary>
        /// From选择按钮点击事件
        /// 打开文件夹选择对话框，选择要移动的源文件夹
        /// </summary>
        private void FromBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseFolder("选择要移动的文件夹");
            if (path != null)
            {
                FromTextBox.Text = path;
            }
        }

        /// <summary>
        /// To选择按钮点击事件
        /// 打开文件夹选择对话框，选择目标文件夹
        /// </summary>
        private void ToBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseFolder("选择目标文件夹");
            if (path != null)
            {
                ToTextBox.Text = path;
            }
        }

        #endregion

        #region 按钮事件

        /// <summary>
        /// 以管理员身份运行按钮点击事件
        /// 请求UAC提权并重新启动程序
        /// </summary>
        private void RunAsAdminButton_Click(object sender, RoutedEventArgs e)
        {
            RestartAsAdmin();
        }

        /// <summary>
        /// 移动按钮点击事件
        /// 异步执行文件夹移动操作，避免阻塞UI线程
        /// </summary>
        private async void MoveButton_Click(object sender, RoutedEventArgs e)
        {
            // 获取源文件夹和目标文件夹路径
            var fromPath = FromTextBox.Text;
            var toPath = ToTextBox.Text;

            // 验证路径不为空
            if (string.IsNullOrWhiteSpace(fromPath))
            {
                MessageBox.Show("请选择要移动的文件夹。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(toPath))
            {
                MessageBox.Show("请选择目标文件夹。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 验证源文件夹存在
            if (!Directory.Exists(fromPath))
            {
                MessageBox.Show("源文件夹不存在，请重新选择。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 确认操作
            var result = MessageBox.Show(
                $"确定要将文件夹移动吗？\n\n从：{fromPath}\n到：{toPath}",
                "确认移动",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            // 禁用按钮，防止重复点击
            MoveButton.IsEnabled = false;
            MoveButton.Content = "移动中...";

            try
            {
                // 异步执行移动操作，在后台线程运行以避免阻塞UI
                await Task.Run(() => MoveDirectory(fromPath, toPath));

                MessageBox.Show("文件夹移动完成！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"移动文件夹时出错：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // 恢复按钮状态
                MoveButton.IsEnabled = true;
                MoveButton.Content = "移动";
            }
        }

        #endregion

        #region 文件操作（异步/多线程支持）

        /// <summary>
        /// 移动文件夹（目录）到目标位置
        /// 如果目标目录已存在，则逐个移动子项
        /// 此方法设计为在后台线程中调用，支持异步/多线程场景
        /// </summary>
        /// <param name="sourceDir">源文件夹路径</param>
        /// <param name="destDir">目标文件夹路径</param>
        private static void MoveDirectory(string sourceDir, string destDir)
        {
            // 如果目标目录不存在，直接移动整个目录（最高效）
            if (!Directory.Exists(destDir))
            {
                Directory.Move(sourceDir, destDir);
                return;
            }

            // 目标目录已存在，需要逐项合并移动
            // 先移动所有子目录
            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                var dirName = Path.GetFileName(dir);
                var destSubDir = Path.Combine(destDir, dirName);
                MoveDirectory(dir, destSubDir);  // 递归处理子目录
            }

            // 再移动所有文件
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var fileName = Path.GetFileName(file);
                var destFile = Path.Combine(destDir, fileName);
                File.Move(file, destFile, overwrite: true);
            }

            // 源目录内容已全部移走，删除空的源目录
            Directory.Delete(sourceDir, recursive: false);
        }

        #endregion
    }
}
