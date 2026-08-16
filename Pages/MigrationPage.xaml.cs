using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using linktool.Services;

namespace linktool.Pages
{
    /// <summary>
    /// 迁移页：单个文件夹的迁移（复制 → 删除源 → 创建目录链接）与终端执行模式。
    /// 路径输入支持历史下拉记忆。
    /// </summary>
    public partial class MigrationPage : UserControl
    {
        private const string FromKey = "migration.from";
        private const string ToKey = "migration.to";

        public MigrationPage()
        {
            InitializeComponent();
            PathHistoryHelper.Load(FromBox, FromKey);
            PathHistoryHelper.Load(ToBox, ToKey);
        }

        /// <summary>回车记录路径历史</summary>
        private void Path_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && sender is ComboBox cb)
                PathHistoryHelper.Remember(cb, ReferenceEquals(cb, FromBox) ? FromKey : ToKey);
        }

        private void FromBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var path = PathHistoryHelper.Browse("选择要迁移的文件夹");
            if (path != null) { FromBox.Text = path; PathHistoryHelper.Remember(FromBox, FromKey); }
        }

        private void ToBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var path = PathHistoryHelper.Browse("选择目标文件夹");
            if (path != null) { ToBox.Text = path; PathHistoryHelper.Remember(ToBox, ToKey); }
        }

        private void RememberPaths()
        {
            PathHistoryHelper.Remember(FromBox, FromKey);
            PathHistoryHelper.Remember(ToBox, ToKey);
        }

        /// <summary>迁移按钮：完整流程</summary>
        private async void MoveButton_Click(object sender, RoutedEventArgs e)
        {
            var fromPath = FromBox.Text?.Trim() ?? "";
            var toPath = ToBox.Text?.Trim() ?? "";

            var error = MoveValidator.Validate(fromPath, toPath);
            if (error != null) { DialogHelper.Warn("提示", error); return; }

            RememberPaths();

            var fromDirName = System.IO.Path.GetFileName(fromPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            var destDir = System.IO.Path.Combine(toPath, fromDirName);

            if (!DialogHelper.Confirm("确认迁移",
                $"确定要将文件夹迁移吗？\n\n从：{fromPath}\n到：{destDir}\n\n操作将：复制文件 → 删除源 → 创建目录链接",
                "迁移", danger: true))
                return;

            SetBusy(true);

            try
            {
                // 阶段0：前置检查
                var denied = await Task.Run(() => FileLockHelper.CheckFileAccessibility(fromPath));
                if (denied.Count > 0)
                {
                    ResetProgress();
                    DialogHelper.Error("权限不足", "以下文件无读取权限，无法继续：\n\n" + JoinTop(denied, 20) + "\n\n请尝试以管理员身份运行或检查文件权限。");
                    return;
                }

                var locked = await Task.Run(() => FileLockHelper.CheckFileLocks(fromPath));
                if (locked.Count > 0)
                {
                    var procs = FileLockHelper.GetLockingProcesses(fromPath);
                    var procInfo = procs.Count > 0 ? "\n\n占用进程：\n" + string.Join("\n", procs.Select(p => $"  - {p.ProcessName} (PID: {p.Id})")) : "";
                    ResetProgress();
                    DialogHelper.Warn("文件被占用", "有程序正在占用源文件夹，需要完全退出相关程序（含后台/托盘程序）后重试：\n\n" + JoinTop(locked, 10) + procInfo);
                    return;
                }

                // 目标同名存在检查
                if (System.IO.Directory.Exists(destDir))
                {
                    bool empty;
                    try { empty = !System.IO.Directory.EnumerateFileSystemEntries(destDir).Any(); }
                    catch { empty = false; }
                    if (!empty)
                    {
                        ResetProgress();
                        DialogHelper.Warn("目标已存在", $"目标文件夹下已存在同名文件夹且包含内容，无法操作：\n{destDir}\n\n请先清空或删除后重试。");
                        return;
                    }
                }

                // 阶段1：复制
                SetProgress(0, 0, "正在统计文件...");
                var allFiles = await Task.Run(() => FileOps.GetAllFiles(fromPath));

                if (allFiles.Count == 0)
                {
                    System.IO.Directory.CreateDirectory(destDir);
                }
                else
                {
                    bool copyOk = false;

                    try
                    {
                        SetProgress(0, allFiles.Count, "正在复制文件...");
                        await Task.Run(() => FileOps.CopyDirectoryFull(fromPath, destDir, allFiles, CancellationToken.None,
                            (c, t, f) => Dispatcher.Invoke(() => SetProgress(c, t, f))));
                        copyOk = true;
                    }
                    catch (Exception)
                    {
                        // robocopy 降级
                        if (AdminHelper.IsRunningAsAdmin())
                        {
                            SetProgress(0, allFiles.Count, "正在以管理员权限使用 robocopy 复制...");
                            copyOk = await Task.Run(() => FileOps.CopyWithRobocopy(fromPath, destDir) <= 7);
                        }
                        else
                        {
                            SetProgress(0, allFiles.Count, "正在请求管理员权限...");
                            copyOk = await Task.Run(() => AdminHelper.RunAsAdminWithTerminal("robocopy.exe",
                                $"\"{fromPath}\" \"{destDir}\" /E /COPYALL /DCOPY:T /R:3 /W:5") <= 7);
                        }
                    }

                    if (!copyOk)
                    {
                        ResetProgress();
                        DialogHelper.Error("复制失败", "文件复制失败，请检查磁盘空间、文件权限等。");
                        return;
                    }
                }

                // 阶段3：删除源
                SetProgress(0, 0, "正在删除源文件夹...");
                var deleted = await Task.Run(() => FileOps.DeleteSourceDirectory(fromPath, CancellationToken.None));
                if (!deleted)
                {
                    DialogHelper.Warn("源文件夹删除失败", $"源文件夹删除失败，请手动清理：\n{fromPath}\n\n文件已成功复制到目标位置，但源文件夹无法自动删除。");
                }

                // 阶段4：创建链接
                SetProgress(0, 0, "正在创建目录链接...");
                await Task.Run(() => JunctionHelper.CreateJunction(fromPath, destDir));

                DialogHelper.Info("成功", $"文件夹迁移完成！\n\n目录链接已创建：\n{fromPath}\n  → 指向 →\n{destDir}");
            }
            catch (JunctionHelperJunctionException ex)
            {
                ResetProgress();
                DialogHelper.Warn("目录链接创建失败", $"文件复制和源目录删除已完成，但目录链接创建失败：\n{ex.Message}\n\n请手动创建：mklink /J \"{fromPath}\" \"{destDir}\"");
            }
            catch (Exception ex)
            {
                ResetProgress();
                DialogHelper.Error("错误", $"迁移文件夹时出错：\n{ex.Message}");
            }
            finally
            {
                SetBusy(false);
                ResetProgress();
            }
        }

        /// <summary>使用终端执行：可见终端顺序 robocopy → rmdir → mklink /J</summary>
        private async void TerminalMoveButton_Click(object sender, RoutedEventArgs e)
        {
            var fromPath = FromBox.Text?.Trim() ?? "";
            var toPath = ToBox.Text?.Trim() ?? "";

            var error = MoveValidator.Validate(fromPath, toPath);
            if (error != null) { DialogHelper.Warn("提示", error); return; }

            RememberPaths();

            var fromDirName = System.IO.Path.GetFileName(fromPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            var destDir = System.IO.Path.Combine(toPath, fromDirName);

            if (System.IO.Directory.Exists(destDir))
            {
                bool empty;
                try { empty = !System.IO.Directory.EnumerateFileSystemEntries(destDir).Any(); }
                catch { empty = false; }
                if (!empty)
                {
                    DialogHelper.Warn("目标已存在", $"目标文件夹下已存在同名文件夹且包含内容：\n{destDir}");
                    return;
                }
            }

            var procs = FileLockHelper.GetProcessesLockingDirectory(fromPath);
            if (procs.Count > 0)
            {
                var list = string.Join("\n", procs.Select(p => $"  - {p.ProcessName} (PID: {p.Id})"));
                if (!DialogHelper.Confirm("检测到进程占用",
                    $"以下进程正在占用源文件夹，可能导致复制不完整：\n{list}\n\n建议先关闭这些进程。是否仍要继续？",
                    "仍要继续"))
                    return;
            }

            if (!DialogHelper.Confirm("确认终端执行",
                $"确定要使用终端执行迁移操作吗？\n\n从：{fromPath}\n到：{destDir}\n\n将打开可见终端依次执行：\n1. robocopy 复制\n2. rmdir 删除源\n3. mklink /J 创建链接",
                "执行", danger: true))
                return;

            SetBusy(true);
            try
            {
                await Task.Run(() =>
                {
                    var batchCmd = $"echo === LinkTool 迁移 === & echo. & " +
                        "echo [1/3] robocopy 复制文件... & " +
                        $"robocopy \"{fromPath}\" \"{destDir}\" /E /COPYALL /DCOPY:T /R:3 /W:5 & echo. & " +
                        "echo [2/3] rmdir 删除源文件夹... & " +
                        $"rmdir /S /Q \"{fromPath}\" & echo. & " +
                        "echo [3/3] mklink /J 创建目录链接... & " +
                        $"mklink /J \"{fromPath}\" \"{destDir}\" & echo. & " +
                        "echo === 操作完成 === & pause";

                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c {batchCmd}",
                        UseShellExecute = true,
                        Verb = AdminHelper.IsRunningAsAdmin() ? "" : "runas",
                        CreateNoWindow = false
                    };
                    System.Diagnostics.Process.Start(psi);
                });
            }
            catch (Exception ex)
            {
                DialogHelper.Error("错误", $"启动终端执行时出错：{ex.Message}");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void SetBusy(bool busy)
        {
            MoveButton.IsEnabled = !busy;
            MoveButton.Content = busy ? "迁移中..." : "迁移";
            TerminalButton.IsEnabled = !busy;
            TerminalButton.Content = busy ? "执行中..." : "使用终端执行";
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

        private static string JoinTop(List<string> items, int top)
        {
            var list = string.Join("\n", items.Take(top));
            if (items.Count > top) list += $"\n... 共 {items.Count} 项";
            return list;
        }
    }
}