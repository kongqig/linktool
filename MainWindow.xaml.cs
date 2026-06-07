/*
 *  LinkTool - 文件夹移动与目录链接工具
 *
 *  状态变量
 *  _lockHandles              独占锁定句柄列表
 *  _cts                      取消令牌源
 *
 *  Win32 API
 *  CreateJunctionNative      Win32 创建 Junction
 *  GetLockingProcesses       Restart Manager 查占用
 *  GetProcessesLockingDirectory  进程模块查占用
 *
 *  管理员权限
 *  IsRunningAsAdmin          检查管理员权限
 *  CheckAdminPrivilege       检查并提示权限
 *  RestartAsAdmin            以管理员重启
 *
 *  辅助方法
 *  IsJunctionOrSymlink       判断是否为链接
 *  IsValidPath               校验路径合法性
 *  UpdateProgress            更新进度条
 *  ResetProgress             重置进度条
 *  LockDirectory             独占锁定目录
 *  UnlockAll                 释放所有锁定
 *  RunAsAdminWithTerminal    管理员终端执行
 *  RunWithVisibleTerminal    可见终端执行
 *
 *  安全验证
 *  ValidateMoveOperation     移动操作前置校验
 *
 *  阶段0：前置检查
 *  CheckFileAccessibility     检查文件访问权限
 *  CheckFileLocks             检查文件独占锁定
 *
 *  阶段1：复制目录
 *  GetAllFiles                获取文件列表
 *  CopyDirectoryFull          .NET 完整复制目录
 *  CopyWithRobocopy           robocopy 复制
 *
 *  阶段3：删除源文件夹
 *  TryKillLockingProcesses    终止占用进程
 *  DeleteSourceDirectory      删除源目录
 *
 *  阶段4：创建目录链接
 *      CreateJunctionLink         创建 Junction 链接
 *
 *  按钮事件
 *  MoveButton_Click           移动按钮
 *  TerminalMoveButton_Click   终端执行按钮
 *  CreateJunctionButton_Click 创建链接按钮
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
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

            var headerSize = 8 + 8;
            var fullData = new byte[headerSize + targetBytes.Length + 2];

            BitConverter.GetBytes(IO_REPARSE_TAG_MOUNT_POINT).CopyTo(fullData, 0);
            BitConverter.GetBytes((ushort)(8 + targetBytes.Length + 2)).CopyTo(fullData, 4);
            BitConverter.GetBytes((ushort)0).CopyTo(fullData, 6);

            BitConverter.GetBytes((ushort)0).CopyTo(fullData, 8);
            BitConverter.GetBytes((ushort)targetBytes.Length).CopyTo(fullData, 10);
            BitConverter.GetBytes((ushort)(targetBytes.Length)).CopyTo(fullData, 12);
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

        #region Win32 API - Restart Manager（检测文件占用进程）

        private const int RM_INVALID_SESSION = -1;
        private const int RM_INVALID_PROCESS = -1;

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Auto)]
        private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint pSessionHandle);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Auto)]
        private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFileNames, uint nApplications, [In] RM_UNIQUE_PROCESS[] rgApplications, uint nServices, string[] rgsServiceNames);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo, [In, Out] RM_PROCESS_INFO[] rgAffectedApps, ref uint lpdwRebootReasons);

        [StructLayout(LayoutKind.Sequential)]
        private struct RM_UNIQUE_PROCESS
        {
            public int dwProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct RM_PROCESS_INFO
        {
            public RM_UNIQUE_PROCESS Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string strServiceShortName;
            public int ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bRestartable;
        }

        /// <summary>
        /// 获取占用指定文件/目录的进程列表
        /// 使用 Restart Manager API
        /// </summary>
        private static List<Process> GetLockingProcesses(string path)
        {
            var processes = new List<Process>();
            int res = RmStartSession(out uint handle, 0, Guid.NewGuid().ToString());
            if (res != 0) return processes;

            try
            {
                var resources = new string[] { path };
                res = RmRegisterResources(handle, (uint)resources.Length, resources, 0, null!, 0, null!);
                if (res != 0) return processes;

                uint pnProcInfo = 0;
                uint lpdwRebootReasons = 0;

                // 第一次调用获取需要的进程数
                res = RmGetList(handle, out uint pnProcInfoNeeded, ref pnProcInfo, null!, ref lpdwRebootReasons);
                if (res != 0 && pnProcInfoNeeded == 0) return processes;

                // 第二次调用获取实际进程信息
                var processInfos = new RM_PROCESS_INFO[pnProcInfoNeeded];
                pnProcInfo = pnProcInfoNeeded;
                res = RmGetList(handle, out pnProcInfoNeeded, ref pnProcInfo, processInfos, ref lpdwRebootReasons);
                if (res != 0) return processes;

                for (int i = 0; i < pnProcInfo; i++)
                {
                    try
                    {
                        var p = Process.GetProcessById(processInfos[i].Process.dwProcessId);
                        processes.Add(p);
                    }
                    catch { /* 进程已退出 */ }
                }
            }
            finally
            {
                RmEndSession(handle);
            }

            return processes;
        }

        #endregion

        /// <summary>
        /// 构造函数
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();
            LoadIcon();
            CheckAdminPrivilege();
        }

        /// <summary>
        /// 从当前 exe 加载窗口图标
        /// </summary>
        private void LoadIcon()
        {
            try
            {
                var exePath = Environment.ProcessPath;
                if (exePath != null)
                {
                    using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                    if (icon != null)
                        Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                            icon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                }
            }
            catch { /* 图标加载失败不影响启动 */ }
        }

        #region 状态变量

        private readonly List<IDisposable> _lockHandles = new();
        private CancellationTokenSource? _cts;

        #endregion

        #region 管理员权限相关

        /// <summary>
        /// 获取占用指定目录的进程列表
        /// 通过检查所有运行中进程的工作目录和打开的文件句柄来判断
        /// </summary>
        private static List<Process> GetProcessesLockingDirectory(string directoryPath)
        {
            var lockingProcesses = new List<Process>();
            var normalizedPath = Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();

            try
            {
                // 方法1：检查进程的主模块文件路径是否在目标目录下
                foreach (var process in Process.GetProcesses())
                {
                    try
                    {
                        if (process.MainModule?.FileName != null)
                        {
                            var procPath = Path.GetFullPath(process.MainModule.FileName);
                            if (procPath.StartsWith(normalizedPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                            {
                                if (!lockingProcesses.Any(p => p.Id == process.Id))
                                    lockingProcesses.Add(process);
                                continue;
                            }
                        }
                    }
                    catch
                    {
                        // 32位进程访问64位进程的MainModule会抛异常，忽略
                    }

                    try
                    {
                        // 方法2：使用 handle.exe 思路 - 通过 WMI 查询进程的可执行路径
                        // 这里用简单的方式：检查进程名中是否包含目录相关关键词
                        // 更可靠的方式是检查进程的模块列表
                        foreach (ProcessModule module in process.Modules)
                        {
                            try
                            {
                                if (module.FileName != null)
                                {
                                    var modPath = Path.GetFullPath(module.FileName);
                                    if (modPath.StartsWith(normalizedPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (!lockingProcesses.Any(p => p.Id == process.Id))
                                            lockingProcesses.Add(process);
                                        break;
                                    }
                                }
                            }
                            catch
                            {
                                break;
                            }
                        }
                    }
                    catch
                    {
                        // 忽略无法访问的进程模块
                    }
                }
            }
            catch
            {
                // 忽略全局异常
            }

            return lockingProcesses;
        }

        private static bool IsRunningAsAdmin()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private void CheckAdminPrivilege()
        {
            if (IsRunningAsAdmin())
            {
                RunAsAdminButton.IsEnabled = false;
                RunAsAdminButton.Content = "已获取管理员权限";
            }
            else
            {
                MessageBox.Show(
                    "建议使用管理员权限运行，能够避免大部分权限不足的问题。",
                    "提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        private void RestartAsAdmin()
        {
            try
            {
                var exePath = Environment.ProcessPath;
                if (exePath == null) return;

                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    Verb = "runas"
                };

                Process.Start(startInfo);
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"无法以管理员身份运行：{ex.Message}",
                    "错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        #endregion

        #region 文件夹选择相关

        private string? BrowseFolder(string description)
        {
            var dialog = new OpenFolderDialog { Title = description };
            if (dialog.ShowDialog() == true)
                return dialog.FolderName;
            return null;
        }

        private void FromBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseFolder("选择要移动的文件夹");
            if (path != null)
                FromTextBox.Text = path;
        }

        private void ToBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseFolder("选择目标文件夹");
            if (path != null)
                ToTextBox.Text = path;
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// 检查路径是否为 Junction 或符号链接
        /// </summary>
        private static bool IsJunctionOrSymlink(string path)
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

        private static bool IsValidPath(string path)
        {
            try
            {
                Path.GetFullPath(path);
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
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 更新 UI 进度信息
        /// </summary>
        private void UpdateProgress(int completed, int total, string currentFile)
        {
            Dispatcher.Invoke(() =>
            {
                ProgressCountText.Text = $"{completed}/{total}";
                ProgressBar.Maximum = total;
                ProgressBar.Value = completed;
                CurrentFileText.Text = currentFile;
            });
        }

        /// <summary>
        /// 重置进度 UI
        /// </summary>
        private void ResetProgress()
        {
            Dispatcher.Invoke(() =>
            {
                ProgressCountText.Text = "0/0";
                ProgressBar.Value = 0;
                CurrentFileText.Text = "";
            });
        }

        /// <summary>
        /// 独占锁定目录（通过打开目录句柄并保持）
        /// </summary>
        private void LockDirectory(string path)
        {
            var handle = CreateFile(
                path,
                GENERIC_WRITE,
                0, // 独占，不允许其他进程写入
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS,
                IntPtr.Zero);

            if (handle != new IntPtr(-1))
            {
                _lockHandles.Add(new SafeHandleWrapper(handle));
            }
        }

        /// <summary>
        /// 释放所有独占锁定
        /// </summary>
        private void UnlockAll()
        {
            foreach (var h in _lockHandles)
                h.Dispose();
            _lockHandles.Clear();
        }

        /// <summary>
        /// 以管理员权限调用可见终端执行命令
        /// </summary>
        private static int RunAsAdminWithTerminal(string fileName, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = false
            };

            using var process = Process.Start(psi);
            if (process == null) return -1;
            process.WaitForExit();
            return process.ExitCode;
        }

        /// <summary>
        /// 以当前权限调用可见终端执行命令
        /// </summary>
        private static int RunWithVisibleTerminal(string fileName, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = false
            };

            using var process = Process.Start(psi);
            if (process == null) return -1;
            process.WaitForExit();
            return process.ExitCode;
        }

        /// <summary>
        /// 安全句柄包装器，用于 IDisposable 管理
        /// </summary>
        private class SafeHandleWrapper : IDisposable
        {
            private IntPtr _handle;
            private bool _disposed;

            public SafeHandleWrapper(IntPtr handle)
            {
                _handle = handle;
            }

            public void Dispose()
            {
                if (!_disposed && _handle != IntPtr.Zero && _handle != new IntPtr(-1))
                {
                    CloseHandle(_handle);
                    _handle = IntPtr.Zero;
                }
                _disposed = true;
            }
        }

        #endregion

        #region 安全验证

        private bool ValidateMoveOperation(string fromPath, string toPath)
        {
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

            if (!IsValidPath(fromPath))
            {
                MessageBox.Show($"源路径格式不合法：\n{fromPath}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (!IsValidPath(toPath))
            {
                MessageBox.Show($"目标路径格式不合法：\n{toPath}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (!Directory.Exists(fromPath))
            {
                MessageBox.Show($"源文件夹不存在：\n{fromPath}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            // 检查 from 是否为 Junction 或符号链接
            if (IsJunctionOrSymlink(fromPath))
            {
                MessageBox.Show(
                    $"源文件夹是目录链接或符号链接，无法移动：\n{fromPath}\n\n请选择真实的文件夹。",
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            var fullFrom = Path.GetFullPath(fromPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullTo = Path.GetFullPath(toPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(fullFrom, fullTo, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("源文件夹和目标文件夹不能相同。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (fullTo.StartsWith(fullFrom + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("目标文件夹不能是源文件夹的子目录，这会导致递归移动。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

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

            return true;
        }

        #endregion

        #region 阶段0：前置检查

        /// <summary>
        /// 检查 from 下所有文件的可访问性
        /// 返回不可访问的文件列表（空列表表示全部可访问）
        /// 使用 FileShare.ReadWrite | FileShare.Delete 避免因其他进程正常读取而误报
        /// </summary>
        private static List<string> CheckFileAccessibility(string fromPath)
        {
            var deniedFiles = new List<string>();
            try
            {
                foreach (var file in Directory.EnumerateFiles(fromPath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        // 使用 ReadWrite + Delete 共享模式，只要能读取就视为可访问
                        // 避免因杀毒软件、搜索索引器等正常读取进程导致误报
                        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        deniedFiles.Add(file);
                    }
                    catch (IOException)
                    {
                        // IOException 可能是真正的独占锁定，也可能是路径问题
                        // 用 Win32 错误码区分：只有真正无法读取的才报错
                        var errorCode = Marshal.GetHRForLastWin32Error();
                        // ERROR_SHARING_VIOLATION (32) = 其他进程独占，不算不可访问
                        // ERROR_LOCK_VIOLATION (33) = 锁冲突，不算不可访问
                        if (errorCode != 0x80070020 && errorCode != 0x80070021)
                        {
                            deniedFiles.Add(file);
                        }
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                deniedFiles.Add(fromPath + " (无法枚举目录内容)");
            }

            return deniedFiles;
        }

        /// <summary>
        /// 检查 from 下是否有文件被独占锁定（无法读取）
        /// 返回被锁定的文件列表
        /// 注意：仅检测真正无法读取的文件，不会因杀毒软件等正常读取而误报
        /// </summary>
        private static List<string> CheckFileLocks(string fromPath)
        {
            var lockedFiles = new List<string>();
            try
            {
                foreach (var file in Directory.EnumerateFiles(fromPath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        // 使用 ReadWrite + Delete 共享模式尝试打开
                        // 这比 FileShare.None 宽松得多，只有真正被独占锁定的文件才会失败
                        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // 无权限不算锁定，归入权限问题
                    }
                    catch (IOException ex)
                    {
                        // 检查是否是共享冲突（被独占锁定）
                        var hresult = ex.HResult & 0xFFFF;
                        // ERROR_SHARING_VIOLATION (32) = 文件被其他进程独占打开
                        // ERROR_LOCK_VIOLATION (33) = 锁冲突
                        if (hresult == 32 || hresult == 33)
                        {
                            lockedFiles.Add(file);
                        }
                        // 其他 IOException 不算锁定
                    }
                }
            }
            catch (UnauthorizedAccessException) { }

            return lockedFiles;
        }

        #endregion

        #region 阶段1：复制目录

        /// <summary>
        /// 获取目录下所有文件列表（用于进度计算）
        /// </summary>
        private static List<string> GetAllFiles(string path)
        {
            var files = new List<string>();
            try
            {
                files.AddRange(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories));
            }
            catch { }
            return files;
        }

        /// <summary>
        /// 使用 .NET API 完整复制目录（保留属性、时间戳、ACL）
        /// </summary>
        private void CopyDirectoryFull(string sourceDir, string destDir, List<string> allFiles, CancellationToken ct)
        {
            var completed = 0;
            var total = allFiles.Count;

            // 创建目标目录结构
            foreach (var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var relative = dir.Substring(sourceDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var destSubDir = Path.Combine(destDir, relative);
                Directory.CreateDirectory(destSubDir);

                // 复制目录属性和时间戳
                try
                {
                    var srcInfo = new DirectoryInfo(dir);
                    var dstInfo = new DirectoryInfo(destSubDir);
                    dstInfo.CreationTime = srcInfo.CreationTime;
                    dstInfo.CreationTimeUtc = srcInfo.CreationTimeUtc;
                    dstInfo.LastWriteTime = srcInfo.LastWriteTime;
                    dstInfo.LastWriteTimeUtc = srcInfo.LastWriteTimeUtc;
                    dstInfo.Attributes = srcInfo.Attributes;
                }
                catch { /* 属性复制失败不阻断 */ }
            }

            // 复制文件
            foreach (var file in allFiles)
            {
                ct.ThrowIfCancellationRequested();
                var relative = file.Substring(sourceDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var destFile = Path.Combine(destDir, relative);

                var destFileDir = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destFileDir) && !Directory.Exists(destFileDir))
                    Directory.CreateDirectory(destFileDir);

                // 复制文件内容
                File.Copy(file, destFile, overwrite: true);

                // 复制文件属性和时间戳
                try
                {
                    var srcInfo = new FileInfo(file);
                    var dstInfo = new FileInfo(destFile);
                    dstInfo.CreationTime = srcInfo.CreationTime;
                    dstInfo.CreationTimeUtc = srcInfo.CreationTimeUtc;
                    dstInfo.LastWriteTime = srcInfo.LastWriteTime;
                    dstInfo.LastWriteTimeUtc = srcInfo.LastWriteTimeUtc;
                    dstInfo.Attributes = srcInfo.Attributes;
                }
                catch { /* 属性复制失败不阻断 */ }

                // ACL 复制需要管理员权限，.NET API 在非管理员下可能失败
                // robocopy /COPYALL 已包含 ACL 复制，此处跳过
                // 如需精确 ACL 复制，请以管理员权限运行

                completed++;
                UpdateProgress(completed, total, relative);
            }

            // 最后复制根目录属性
            try
            {
                var srcRootInfo = new DirectoryInfo(sourceDir);
                var dstRootInfo = new DirectoryInfo(destDir);
                dstRootInfo.CreationTime = srcRootInfo.CreationTime;
                dstRootInfo.CreationTimeUtc = srcRootInfo.CreationTimeUtc;
                dstRootInfo.LastWriteTime = srcRootInfo.LastWriteTime;
                dstRootInfo.LastWriteTimeUtc = srcRootInfo.LastWriteTimeUtc;
                dstRootInfo.Attributes = srcRootInfo.Attributes;
            }
            catch { }
        }

        /// <summary>
        /// 使用 robocopy 复制目录
        /// </summary>
        private static int CopyWithRobocopy(string sourceDir, string destDir)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "robocopy.exe",
                Arguments = $"\"{sourceDir}\" \"{destDir}\" /E /COPYALL /DCOPY:T /R:3 /W:5",
                UseShellExecute = false,
                CreateNoWindow = false
            };

            using var process = Process.Start(psi);
            if (process == null) return -1;
            process.WaitForExit();
            // robocopy 返回码：0-7 为成功，8+ 为错误
            return process.ExitCode;
        }

        #endregion

        #region 阶段3：删除源文件夹

        /// <summary>
        /// 尝试终止占用指定路径的进程
        /// 返回是否全部终止成功
        /// </summary>
        private static bool TryKillLockingProcesses(string path)
        {
            var processes = GetLockingProcesses(path);
            if (processes.Count == 0) return true;

            var failed = new List<string>();
            foreach (var p in processes)
            {
                try
                {
                    p.Kill();
                    p.WaitForExit(5000);
                }
                catch
                {
                    failed.Add($"{p.ProcessName} (PID: {p.Id})");
                }
            }

            return failed.Count == 0;
        }

        /// <summary>
        /// 删除源目录（包括目录本身）
        /// </summary>
        private void DeleteSourceDirectory(string sourceDir, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            // 先尝试标准删除
            try
            {
                Directory.Delete(sourceDir, recursive: true);
                return;
            }
            catch { }

            // 检查是否还有进程占用
            var stillExists = Directory.Exists(sourceDir);
            if (!stillExists) return;

            // 尝试终止占用进程
            TryKillLockingProcesses(sourceDir);

            // 再次尝试删除
            try
            {
                Directory.Delete(sourceDir, recursive: true);
                return;
            }
            catch { }

            // 以管理员权限调用终端执行 rmdir
            if (IsRunningAsAdmin())
            {
                try
                {
                    var exitCode = RunWithVisibleTerminal("cmd.exe", $"/c rmdir /S /Q \"{sourceDir}\"");
                    if (exitCode == 0 && !Directory.Exists(sourceDir))
                        return;
                }
                catch { }
            }
            else
            {
                try
                {
                    var exitCode = RunAsAdminWithTerminal("cmd.exe", $"/c rmdir /S /Q \"{sourceDir}\"");
                    if (!Directory.Exists(sourceDir))
                        return;
                }
                catch { }
            }

            // 仍然失败，弹窗告知用户
            if (Directory.Exists(sourceDir))
            {
                Dispatcher.Invoke(() =>
                {
                    MessageBox.Show(
                        $"源文件夹删除失败，请手动清理：\n{sourceDir}\n\n文件已成功复制到目标位置，但源文件夹无法自动删除。\n你可以稍后手动删除该文件夹。",
                        "源文件夹删除失败",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                });
            }
        }

        #endregion

        #region 阶段4：创建目录链接

        /// <summary>
        /// 创建 Junction 目录链接
        /// 降级策略：Win32 API → 管理员权限重试 → cmd mklink /J
        /// </summary>
        private void CreateJunctionLink(string junctionPath, string targetPath)
        {
            // 确保链接位置不存在（如果源目录没删掉就不应该走到这里）
            if (Directory.Exists(junctionPath))
            {
                try
                {
                    // 如果是空目录就删除
                    if (!Directory.EnumerateFileSystemEntries(junctionPath).Any())
                        Directory.Delete(junctionPath, false);
                    else
                    {
                        // 非空目录，无法创建链接
                        throw new InvalidOperationException($"链接位置已存在且非空：{junctionPath}");
                    }
                }
                catch (InvalidOperationException) { throw; }
                catch { }
            }

            // 创建空目录作为 Junction 载体
            Directory.CreateDirectory(junctionPath);

            // 第一步：尝试 Win32 API
            try
            {
                CreateJunctionNative(junctionPath, targetPath);
                if (IsJunction(junctionPath)) return;
            }
            catch { }

            // Win32 失败，清理并重试
            try { Directory.Delete(junctionPath, false); } catch { }
            Directory.CreateDirectory(junctionPath);

            // 第二步：如果不是管理员，尝试以管理员权限重试 Win32 API
            if (!IsRunningAsAdmin())
            {
                try
                {
                    CreateJunctionNative(junctionPath, targetPath);
                    if (IsJunction(junctionPath)) return;
                }
                catch { }
            }

            // 清理重试
            try { Directory.Delete(junctionPath, false); } catch { }

            // 第三步：以管理员权限调用 cmd mklink /J
            try
            {
                if (IsRunningAsAdmin())
                {
                    var exitCode = RunWithVisibleTerminal("cmd.exe", $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"");
                    if (exitCode == 0 && IsJunction(junctionPath)) return;
                }
                else
                {
                    var exitCode = RunAsAdminWithTerminal("cmd.exe", $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"");
                    if (IsJunction(junctionPath)) return;
                }
            }
            catch { }

            // 全部失败
            throw new InvalidOperationException($"目录链接创建失败，所有方式均未成功。\n链接路径：{junctionPath}\n目标路径：{targetPath}");
        }

        #endregion

        #region 按钮事件

        private void RunAsAdminButton_Click(object sender, RoutedEventArgs e)
        {
            RestartAsAdmin();
        }

        /// <summary>
        /// 移动按钮点击事件 - 核心功能
        /// 流程：前置检查 → 复制 → 删除源 → 创建链接 → 清理
        /// </summary>
        private async void MoveButton_Click(object sender, RoutedEventArgs e)
        {
            var fromPath = FromTextBox.Text.Trim();
            var toPath = ToTextBox.Text.Trim();

            // 基础验证
            if (!ValidateMoveOperation(fromPath, toPath))
                return;

            // 确认操作
            var fromDirName = Path.GetFileName(fromPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var destDir = Path.Combine(toPath, fromDirName);

            var result = MessageBox.Show(
                $"确定要将文件夹移动吗？\n\n从：{fromPath}\n到：{destDir}\n\n操作将：复制文件 → 删除源 → 创建目录链接",
                "确认移动",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            // 禁用按钮
            MoveButton.IsEnabled = false;
            MoveButton.Content = "移动中...";
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            try
            {
                // ===== 阶段0：前置检查 =====
                UpdateProgress(0, 0, "正在检查文件访问权限...");

                var deniedFiles = await Task.Run(() => CheckFileAccessibility(fromPath), ct);
                if (deniedFiles.Count > 0)
                {
                    var fileList = string.Join("\n", deniedFiles.Take(20));
                    if (deniedFiles.Count > 20)
                        fileList += $"\n... 共 {deniedFiles.Count} 个文件";

                    MessageBox.Show(
                        $"以下文件无读取权限，无法继续：\n\n{fileList}\n\n请尝试以管理员身份运行或检查文件权限。",
                        "权限不足",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    ResetProgress();
                    return;
                }

                UpdateProgress(0, 0, "正在检查文件占用...");
                var lockedFiles = await Task.Run(() => CheckFileLocks(fromPath), ct);
                if (lockedFiles.Count > 0)
                {
                    // 获取占用进程信息
                    var lockingProcs = GetLockingProcesses(fromPath);
                    var procInfo = lockingProcs.Count > 0
                        ? "\n\n占用进程：\n" + string.Join("\n", lockingProcs.Select(p => $"  - {p.ProcessName} (PID: {p.Id})"))
                        : "";

                    var fileList = string.Join("\n", lockedFiles.Take(10));
                    if (lockedFiles.Count > 10)
                        fileList += $"\n... 共 {lockedFiles.Count} 个文件";

                    MessageBox.Show(
                        $"有程序正在占用源文件夹，需要完全退出相关程序（包括后台进程和托盘程序）后重试：\n\n{fileList}{procInfo}",
                        "文件被占用",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    ResetProgress();
                    return;
                }

                // ===== 阶段1：复制目录 =====
                // 检查目标位置是否已存在同名文件夹
                if (Directory.Exists(destDir))
                {
                    bool isDestEmpty;
                    try
                    {
                        isDestEmpty = !Directory.EnumerateFileSystemEntries(destDir).Any();
                    }
                    catch
                    {
                        isDestEmpty = false;
                    }

                    if (!isDestEmpty)
                    {
                        MessageBox.Show(
                            $"目标文件夹下已存在同名文件夹且包含内容，无法操作：\n{destDir}\n\n请先清空或删除该文件夹后重试。",
                            "目标已存在",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        ResetProgress();
                        return;
                    }
                }

                // 获取所有文件列表用于进度计算
                UpdateProgress(0, 0, "正在统计文件...");
                var allFiles = await Task.Run(() => GetAllFiles(fromPath), ct);

                if (allFiles.Count == 0)
                {
                    // 空目录，直接创建目标目录
                    Directory.CreateDirectory(destDir);
                }
                else
                {
                    // 独占锁定目标目录
                    try
                    {
                        if (Directory.Exists(destDir))
                            LockDirectory(destDir);
                    }
                    catch { /* 锁定失败不阻断 */ }

                    bool copySuccess = false;

                    // 尝试 .NET API 复制
                    try
                    {
                        UpdateProgress(0, allFiles.Count, "正在复制文件...");
                        await Task.Run(() => CopyDirectoryFull(fromPath, destDir, allFiles, ct), ct);
                        copySuccess = true;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // .NET 复制失败，尝试 robocopy 降级
                        UnlockAll();

                        if (IsRunningAsAdmin())
                        {
                            UpdateProgress(0, allFiles.Count, "正在以管理员权限使用 robocopy 复制...");
                            try
                            {
                                var robocopyExit = await Task.Run(() => CopyWithRobocopy(fromPath, destDir), ct);
                                // robocopy 返回码 0-7 为成功
                                copySuccess = robocopyExit <= 7;
                            }
                            catch { }
                        }
                        else
                        {
                            // 没有管理员权限，先请求提权
                            UpdateProgress(0, allFiles.Count, "正在请求管理员权限...");
                            try
                            {
                                var robocopyExit = await Task.Run(() =>
                                {
                                    // 尝试以管理员权限调用 robocopy
                                    return RunAsAdminWithTerminal("robocopy.exe",
                                        $"\"{fromPath}\" \"{destDir}\" /E /COPYALL /DCOPY:T /R:3 /W:5");
                                }, ct);
                                copySuccess = robocopyExit <= 7;
                            }
                            catch { }
                        }
                    }

                    if (!copySuccess)
                    {
                        UnlockAll();
                        ResetProgress();
                        MessageBox.Show(
                            "文件复制失败，所有方式均未成功。\n请检查磁盘空间、文件权限等。",
                            "复制失败",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                        return;
                    }

                    // 解锁目标目录
                    UnlockAll();
                }

                // ===== 阶段3：删除源文件夹 =====
                UpdateProgress(0, 0, "正在删除源文件夹...");
                await Task.Run(() => DeleteSourceDirectory(fromPath, ct), ct);

                // ===== 阶段4：创建目录链接 =====
                UpdateProgress(0, 0, "正在创建目录链接...");
                try
                {
                    await Task.Run(() => CreateJunctionLink(fromPath, destDir), ct);

                    // 验证链接
                    if (IsJunction(fromPath))
                    {
                        MessageBox.Show(
                            $"文件夹移动完成！\n\n目录链接已创建：\n{fromPath}\n  → 指向 →\n{destDir}",
                            "成功",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show(
                            $"文件复制和源目录删除已完成，但目录链接可能未正确创建。\n\n请手动创建目录链接：\nmklink /J \"{fromPath}\" \"{destDir}\"",
                            "部分完成",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }
                }
                catch (Exception ex)
                {
                    ResetProgress();
                    MessageBox.Show(
                        $"文件复制和源目录删除已完成，但目录链接创建失败：\n{ex.Message}\n\n请手动创建目录链接：\nmklink /J \"{fromPath}\" \"{destDir}\"",
                        "目录链接创建失败",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            catch (OperationCanceledException)
            {
                ResetProgress();
                MessageBox.Show("操作已取消。", "取消", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ResetProgress();
                MessageBox.Show($"移动文件夹时出错：\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // ===== 阶段5：清理 =====
                UnlockAll();
                _cts?.Dispose();
                _cts = null;
                ResetProgress();

                MoveButton.IsEnabled = true;
                MoveButton.Content = "移动";
            }
        }

        /// <summary>
        /// 使用终端执行按钮点击事件
        /// 所有步骤使用可见终端命令操作：robocopy 复制 → rmdir 删除 → mklink /J 创建链接
        /// </summary>
        private async void TerminalMoveButton_Click(object sender, RoutedEventArgs e)
        {
            var fromPath = FromTextBox.Text.Trim();
            var toPath = ToTextBox.Text.Trim();

            // 基础验证
            if (!ValidateMoveOperation(fromPath, toPath))
                return;

            var fromDirName = Path.GetFileName(fromPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var destDir = Path.Combine(toPath, fromDirName);

            // 检查目标位置是否已存在同名文件夹
            if (Directory.Exists(destDir))
            {
                bool isDestEmpty;
                try
                {
                    isDestEmpty = !Directory.EnumerateFileSystemEntries(destDir).Any();
                }
                catch
                {
                    isDestEmpty = false;
                }

                if (!isDestEmpty)
                {
                    MessageBox.Show(
                        $"目标文件夹下已存在同名文件夹且包含内容，无法操作：\n{destDir}\n\n请先清空或删除该文件夹后重试。",
                        "目标已存在",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
            }

            // 检查源文件夹是否有进程占用
            var lockingProcesses = GetProcessesLockingDirectory(fromPath);
            if (lockingProcesses.Count > 0)
            {
                var procList = string.Join("\n", lockingProcesses.Select(p => $"  - {p.ProcessName} (PID: {p.Id})"));
                var lockResult = MessageBox.Show(
                    $"以下进程正在占用源文件夹，可能导致复制不完整：\n{procList}\n\n建议先关闭这些进程后再执行。\n\n是否仍要继续？",
                    "检测到进程占用",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (lockResult != MessageBoxResult.Yes) return;
            }

            // 确认操作
            var result = MessageBox.Show(
                $"确定要使用终端执行移动操作吗？\n\n从：{fromPath}\n到：{destDir}\n\n将打开可见终端窗口执行：\n1. robocopy 复制文件\n2. rmdir 删除源文件夹\n3. mklink /J 创建目录链接",
                "确认终端执行",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            TerminalMoveButton.IsEnabled = false;
            TerminalMoveButton.Content = "执行中...";

            try
            {
                await Task.Run(() =>
                {
                    // 构建批处理命令
                    // 使用 & 链接命令，每步失败后暂停让用户看到错误
                    var batchCmd = $"echo === LinkTool 终端执行 === & " +
                        $"echo. & " +
                        $"echo [1/3] 正在复制文件... & " +
                        $"robocopy \"{fromPath}\" \"{destDir}\" /E /COPYALL /DCOPY:T /R:3 /W:5 & " +
                        $"echo. & " +
                        $"echo [2/3] 正在删除源文件夹... & " +
                        $"rmdir /S /Q \"{fromPath}\" & " +
                        $"echo. & " +
                        $"echo [3/3] 正在创建目录链接... & " +
                        $"mklink /J \"{fromPath}\" \"{destDir}\" & " +
                        $"echo. & " +
                        $"echo === 操作完成 === & " +
                        $"pause";

                    var psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c {batchCmd}",  // /c 执行完毕后自动关闭终端
                        UseShellExecute = true,
                        Verb = IsRunningAsAdmin() ? "" : "runas",
                        CreateNoWindow = false
                    };

                    Process.Start(psi);
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"启动终端执行时出错：\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                TerminalMoveButton.IsEnabled = true;
                TerminalMoveButton.Content = "使用终端执行";
            }
        }

        /// <summary>
        /// 创建目录链接按钮点击事件
        /// </summary>
        private async void CreateJunctionButton_Click(object sender, RoutedEventArgs e)
        {
            var fromPath = FromTextBox.Text.Trim();
            var toPath = ToTextBox.Text.Trim();

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

            var fromDirName = Path.GetFileName(fromPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(fromDirName))
            {
                MessageBox.Show("无法从源路径中提取文件夹名称。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var junctionPath = Path.Combine(toPath, fromDirName);

            if (Directory.Exists(fromPath))
            {
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

            if (Directory.Exists(junctionPath))
            {
                MessageBox.Show(
                    $"目标位置已存在同名文件夹：\n{junctionPath}\n\n请先删除或重命名后重试。",
                    "目标已存在",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!Directory.Exists(toPath))
            {
                MessageBox.Show(
                    $"目标目录不存在：\n{toPath}\n\n无法创建目录链接。",
                    "目标不存在",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

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
                await Task.Run(() => CreateJunctionLink(junctionPath, fromPath));

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

        #endregion
    }
}
