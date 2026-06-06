using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
        #region Win32 API - Junction 创建

        // Win32 常量
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        private const int FSCTL_SET_REPARSE_POINT = 0x000900A4;
        private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice, uint dwIoControlCode,
            byte[] lpInBuffer, uint nInBufferSize,
            IntPtr lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        /// <summary>
        /// 通过 Win32 API 创建 Junction（目录联接）
        /// </summary>
        private static void CreateJunctionNative(string junctionPath, string targetPath)
        {
            var targetPathFormatted = @"\??\" + targetPath.TrimEnd('\\') + "\\\0";
            var targetBytes = System.Text.Encoding.Unicode.GetBytes(targetPathFormatted);

            // REPARSE_DATA_BUFFER 结构
            var data = new byte[8 + targetBytes.Length + 2]; // 8 = ReparseTag(4) + ReparseDataLength(2) + Reserved(2), +2 for SubstituteNameLength padding
            BitConverter.GetBytes(IO_REPARSE_TAG_MOUNT_POINT).CopyTo(data, 0);
            BitConverter.GetBytes((ushort)(targetBytes.Length + 2)).CopyTo(data, 4); // ReparseDataLength
            BitConverter.GetBytes((ushort)0).CopyTo(data, 6); // Reserved

            // SubstituteNameOffset(2) + SubstituteNameLength(2) + PrintNameOffset(2) + PrintNameLength(2)
            var headerSize = 8 + 8; // ReparseDataBuffer header + SymbolicLinkReparseBuffer header
            var fullData = new byte[headerSize + targetBytes.Length + 2];

            BitConverter.GetBytes(IO_REPARSE_TAG_MOUNT_POINT).CopyTo(fullData, 0);
            BitConverter.GetBytes((ushort)(8 + targetBytes.Length + 2)).CopyTo(fullData, 4);
            BitConverter.GetBytes((ushort)0).CopyTo(fullData, 6);

            // SubstituteNameOffset
            BitConverter.GetBytes((ushort)0).CopyTo(fullData, 8);
            // SubstituteNameLength
            BitConverter.GetBytes((ushort)targetBytes.Length).CopyTo(fullData, 10);
            // PrintNameOffset
            BitConverter.GetBytes((ushort)(targetBytes.Length)).CopyTo(fullData, 12);
            // PrintNameLength
            BitConverter.GetBytes((ushort)0).CopyTo(fullData, 14);

            targetBytes.CopyTo(fullData, 16);

            var handle = CreateFile(
                junctionPath,
                GENERIC_WRITE,
                0,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS,
                IntPtr.Zero);

            if (handle == new IntPtr(-1))
                Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());

            try
            {
                if (!DeviceIoControl(handle, FSCTL_SET_REPARSE_POINT, fullData, (uint)fullData.Length,
                    IntPtr.Zero, 0, out _, IntPtr.Zero))
                {
                    Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
                }
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        #endregion
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

        #region 安全验证

        /// <summary>
        /// 对移动操作进行完整的安全验证
        /// 检查路径格式、目录存在性、权限、冲突等
        /// </summary>
        /// <param name="fromPath">源文件夹路径</param>
        /// <param name="toPath">目标文件夹路径</param>
        /// <returns>验证通过返回true，否则显示错误信息并返回false</returns>
        private bool ValidateMoveOperation(string fromPath, string toPath)
        {
            // 1. 路径非空检查
            if (string.IsNullOrWhiteSpace(fromPath))
            {
                MessageBox.Show("请输入或选择要移动的文件夹路径。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (string.IsNullOrWhiteSpace(toPath))
            {
                MessageBox.Show("请输入或选择目标文件夹路径。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            // 2. 路径格式合法性检查
            if (!IsValidPath(fromPath))
            {
                MessageBox.Show($"源路径格式不合法：\n{fromPath}\n\n请检查路径中是否包含非法字符。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (!IsValidPath(toPath))
            {
                MessageBox.Show($"目标路径格式不合法：\n{toPath}\n\n请检查路径中是否包含非法字符。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            // 3. 源目录必须存在
            if (!Directory.Exists(fromPath))
            {
                MessageBox.Show($"源文件夹不存在：\n{fromPath}\n\n请确认路径是否正确。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            // 4. 源和目标不能相同
            var fullFrom = Path.GetFullPath(fromPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullTo = Path.GetFullPath(toPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(fullFrom, fullTo, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("源文件夹和目标文件夹不能相同。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            // 5. 目标不能是源的子目录（会导致递归移动）
            if (fullTo.StartsWith(fullFrom + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("目标文件夹不能是源文件夹的子目录，这会导致递归移动。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            // 6. 检查源目录读取权限
            try
            {
                Directory.GetDirectories(fromPath);
                Directory.GetFiles(fromPath);
            }
            catch (UnauthorizedAccessException)
            {
                MessageBox.Show($"没有源文件夹的访问权限：\n{fromPath}\n\n请尝试以管理员身份运行。", "权限不足", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            // 7. 检查目标父目录的写入权限
            var toParent = Path.GetDirectoryName(fullTo);
            if (!string.IsNullOrEmpty(toParent))
            {
                if (!Directory.Exists(toParent))
                {
                    MessageBox.Show($"目标路径的父目录不存在：\n{toParent}\n\n请先创建该目录或选择其他目标。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }

                try
                {
                    // 尝试在父目录中创建并删除临时文件来验证写入权限
                    var tempFile = Path.Combine(toParent, $"~linktool_test_{Guid.NewGuid():N}");
                    File.WriteAllText(tempFile, "");
                    File.Delete(tempFile);
                }
                catch (UnauthorizedAccessException)
                {
                    MessageBox.Show($"没有目标路径父目录的写入权限：\n{toParent}\n\n请尝试以管理员身份运行。", "权限不足", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }
            }

            // 8. 检查驱动器是否存在
            var fromRoot = Path.GetPathRoot(fromPath);
            var toRoot = Path.GetPathRoot(toPath);

            if (!string.IsNullOrEmpty(fromRoot) && !Directory.Exists(fromRoot))
            {
                MessageBox.Show($"源路径所在的驱动器不存在：\n{fromRoot}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (!string.IsNullOrEmpty(toRoot) && !Directory.Exists(toRoot))
            {
                MessageBox.Show($"目标路径所在的驱动器不存在：\n{toRoot}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            // 9. 如果目标目录已存在，提示用户确认合并
            if (Directory.Exists(toPath))
            {
                var mergeResult = MessageBox.Show(
                    $"目标文件夹已存在：\n{toPath}\n\n源文件夹的内容将被合并到目标文件夹中，同名文件将被覆盖。\n\n是否继续？",
                    "目标已存在",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (mergeResult != MessageBoxResult.Yes)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// 检查路径格式是否合法
        /// 验证路径中不包含非法字符，且是合法的绝对路径
        /// </summary>
        private static bool IsValidPath(string path)
        {
            try
            {
                // Path.GetFullPath 会对非法路径抛出异常
                var fullPath = Path.GetFullPath(path);

                // 检查非法字符
                var fileName = Path.GetFileName(path);
                if (!string.IsNullOrEmpty(fileName))
                {
                    var invalidChars = Path.GetInvalidFileNameChars();
                    foreach (var c in fileName)
                    {
                        if (Array.IndexOf(invalidChars, c) >= 0)
                            return false;
                    }
                }

                return true;
            }
            catch (Exception)
            {
                return false;
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
            var fromPath = FromTextBox.Text.Trim();
            var toPath = ToTextBox.Text.Trim();

            // 安全验证
            if (!ValidateMoveOperation(fromPath, toPath))
                return;

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
            catch (UnauthorizedAccessException ex)
            {
                MessageBox.Show($"权限不足，无法完成移动：\n{ex.Message}\n\n请尝试以管理员身份运行。", "权限不足", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (IOException ex) when (ex.HResult == unchecked((int)0x80070050))
            {
                // ERROR_FILE_EXISTS (0x50) - 文件已存在
                MessageBox.Show($"目标位置已存在同名文件：\n{ex.Message}", "文件已存在", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (IOException ex) when (ex.HResult == unchecked((int)0x80070005))
            {
                // E_ACCESSDENIED - 拒绝访问
                MessageBox.Show($"访问被拒绝：\n{ex.Message}\n\n请尝试以管理员身份运行。", "拒绝访问", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (DirectoryNotFoundException ex)
            {
                MessageBox.Show($"目录未找到：\n{ex.Message}\n\n可能已被其他程序删除或移动。", "目录不存在", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (PathTooLongException ex)
            {
                MessageBox.Show($"路径过长：\n{ex.Message}\n\nWindows路径长度限制为260个字符。", "路径过长", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (IOException ex)
            {
                MessageBox.Show($"文件操作出错：\n{ex.Message}", "IO错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"移动文件夹时出错：\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // 恢复按钮状态
                MoveButton.IsEnabled = true;
                MoveButton.Content = "移动";
            }
        }

        /// <summary>
        /// 创建目录链接按钮点击事件
        /// 在目标位置创建 Junction 链接指向源目录
        /// 降级策略：Win32 API → 管理员权限重试 → cmd mklink 前台终端
        /// </summary>
        private async void CreateJunctionButton_Click(object sender, RoutedEventArgs e)
        {
            var fromPath = FromTextBox.Text.Trim();
            var toPath = ToTextBox.Text.Trim();

            // 基本验证
            if (string.IsNullOrWhiteSpace(fromPath))
            {
                MessageBox.Show("请输入或选择源文件夹路径。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(toPath))
            {
                MessageBox.Show("请输入或选择目标文件夹路径。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!IsValidPath(fromPath) || !IsValidPath(toPath))
            {
                MessageBox.Show("路径格式不合法，请检查是否包含非法字符。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 确定 junction 链接路径：toPath 下的 from 文件夹名
            var fromDirName = Path.GetFileName(fromPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(fromDirName))
            {
                MessageBox.Show("无法从源路径中提取文件夹名称。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var junctionPath = Path.Combine(toPath, fromDirName);

            // 检查 from 原目录是否还存在
            if (Directory.Exists(fromPath))
            {
                // 检查 from 目录内部是否有内容
                try
                {
                    var hasContent = Directory.GetFileSystemEntries(fromPath).Length > 0;
                    if (hasContent)
                    {
                        MessageBox.Show(
                            $"源文件夹仍然存在且包含内容：\n{fromPath}\n\n请先移动文件夹内容后再创建目录链接。",
                            "无法创建",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        return;
                    }

                    // 空目录，删除它
                    Directory.Delete(fromPath, recursive: false);
                }
                catch (UnauthorizedAccessException)
                {
                    MessageBox.Show(
                        $"没有源文件夹的访问权限：\n{fromPath}\n\n请尝试以管理员身份运行。",
                        "权限不足",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }
            }

            // 检查 to 目录下是否已存在目标文件夹
            if (Directory.Exists(junctionPath))
            {
                MessageBox.Show(
                    $"目标位置已存在同名文件夹：\n{junctionPath}\n\n请先删除或重命名后重试。",
                    "目标已存在",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            // 检查 to 目录本身是否存在
            if (!Directory.Exists(toPath))
            {
                MessageBox.Show(
                    $"目标目录不存在：\n{toPath}\n\n无法创建目录链接。",
                    "目标不存在",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            // 确认操作
            var confirm = MessageBox.Show(
                $"将创建目录链接（Junction）：\n\n{junctionPath}\n  → 指向 →\n{fromPath}\n\n是否继续？",
                "确认创建目录链接",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            CreateJunctionButton.IsEnabled = false;
            CreateJunctionButton.Content = "创建中...";

            try
            {
                await Task.Run(() =>
                {
                    // 第一步：创建空目录作为 Junction 载体
                    Directory.CreateDirectory(junctionPath);

                    // 第二步：尝试用 Win32 API 创建 Junction
                    try
                    {
                        CreateJunctionNative(junctionPath, fromPath);
                        return; // 成功
                    }
                    catch (Exception ex1)
                    {
                        // Win32 API 失败，尝试删除刚创建的目录重试
                        try { Directory.Delete(junctionPath, false); } catch { }
                        Directory.CreateDirectory(junctionPath);

                        // 第三步：如果不是管理员，尝试以管理员权限重试
                        if (!IsRunningAsAdmin())
                        {
                            try
                            {
                                CreateJunctionNative(junctionPath, fromPath);
                                return; // 管理员权限下成功
                            }
                            catch
                            {
                                // 仍然失败，降级到 cmd
                            }
                        }

                        // 清理重试
                        try { Directory.Delete(junctionPath, false); } catch { }

                        // 第四步：使用 cmd 前台终端执行 mklink /J
                        var psi = new ProcessStartInfo
                        {
                            FileName = "cmd.exe",
                            Arguments = $"/k mklink /J \"{junctionPath}\" \"{fromPath}\"",
                            UseShellExecute = false,
                            CreateNoWindow = false  // 前台窗口，用户可见
                        };

                        var process = Process.Start(psi);
                        if (process != null)
                        {
                            process.WaitForExit();
                            if (process.ExitCode == 0 && Directory.Exists(junctionPath))
                                return; // cmd 方式成功
                        }

                        throw new InvalidOperationException(
                            $"创建目录链接失败。\n\nWin32 API 错误：{ex1.Message}\n\ncmd mklink 也未能成功创建链接。");
                    }
                });

                // 验证 Junction 是否创建成功
                if (IsJunction(junctionPath))
                {
                    MessageBox.Show(
                        $"目录链接创建成功！\n\n{junctionPath}\n  → 指向 →\n{fromPath}",
                        "成功",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(
                        "目录链接可能未正确创建，请手动验证。",
                        "警告",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"创建目录链接时出错：\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                CreateJunctionButton.IsEnabled = true;
                CreateJunctionButton.Content = "创建目录链接";
            }
        }

        /// <summary>
        /// 检查指定路径是否为 Junction（目录联接）
        /// </summary>
        private static bool IsJunction(string path)
        {
            try
            {
                var di = new DirectoryInfo(path);
                return di.Attributes.HasFlag(FileAttributes.ReparsePoint);
            }
            catch
            {
                return false;
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
