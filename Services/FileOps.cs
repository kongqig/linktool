using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace linktool.Services
{
    /// <summary>进度回调：completed 已完成数，total 总数，currentFile 当前文件</summary>
    public delegate void ProgressHandler(int completed, int total, string currentFile);

    /// <summary>
    /// 纯文件操作：统计、复制（.NET/robocopy）、删除源目录、终止占用进程。
    /// 不含任何 UI 交互，由页面层负责提示。
    /// </summary>
    public static class FileOps
    {
        /// <summary>获取目录下所有文件（非递归异常安全）</summary>
        public static List<string> GetAllFiles(string path)
        {
            var files = new List<string>();
            try { files.AddRange(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)); }
            catch { }
            return files;
        }

        /// <summary>使用 .NET API 完整复制目录（保留属性/时间戳），通过 progress 回调上报进度</summary>
        public static void CopyDirectoryFull(string sourceDir, string destDir, List<string> allFiles, CancellationToken ct, ProgressHandler? progress)
        {
            var completed = 0;
            var total = allFiles.Count;

            foreach (var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var relative = dir.Substring(sourceDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var destSubDir = Path.Combine(destDir, relative);
                Directory.CreateDirectory(destSubDir);
                CopyDirMeta(new DirectoryInfo(dir), new DirectoryInfo(destSubDir));
            }

            foreach (var file in allFiles)
            {
                ct.ThrowIfCancellationRequested();
                var relative = file.Substring(sourceDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var destFile = Path.Combine(destDir, relative);
                var destDirPath = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destDirPath) && !Directory.Exists(destDirPath))
                    Directory.CreateDirectory(destDirPath);

                File.Copy(file, destFile, overwrite: true);
                CopyFileMeta(new FileInfo(file), new FileInfo(destFile));

                completed++;
                progress?.Invoke(completed, total, relative);
            }

            CopyDirMeta(new DirectoryInfo(sourceDir), new DirectoryInfo(destDir));
        }

        /// <summary>使用 robocopy 复制（完整属性）。返回 robocopy 退出码（0-7 成功）</summary>
        public static int CopyWithRobocopy(string sourceDir, string destDir)
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
            return process.ExitCode;
        }

        /// <summary>尝试终止占用指定路径的进程，返回是否全部成功</summary>
        public static bool TryKillLockingProcesses(string path)
        {
            var processes = FileLockHelper.GetLockingProcesses(path);
            if (processes.Count == 0) return true;
            var failed = false;
            foreach (var p in processes)
            {
                try { p.Kill(); p.WaitForExit(5000); }
                catch { failed = true; }
            }
            return !failed;
        }

        /// <summary>
        /// 删除源目录（标准删除 → 终止占用进程重试 → 管理员 rmdir /S /Q）。
        /// allowAdminFallback=false 时跳过强杀进程与 admin/UAC 降级（用于批量静默场景，
        /// 避免逐行弹提权、也避免未经确认强制结束用户程序）。
        /// 返回 true=已删除，false=目录仍存在（由调用方提示用户手动清理）。
        /// </summary>
        public static bool DeleteSourceDirectory(string sourceDir, CancellationToken ct, bool allowAdminFallback = true)
        {
            ct.ThrowIfCancellationRequested();

            if (TryDeleteDirect(sourceDir)) return true;

            if (!Directory.Exists(sourceDir)) return true;

            if (allowAdminFallback)
            {
                // 仅交互式场景尝试终止占用进程，再重试删除
                TryKillLockingProcesses(sourceDir);
                if (TryDeleteDirect(sourceDir)) return true;

                try
                {
                    var exitCode = AdminHelper.IsRunningAsAdmin()
                        ? AdminHelper.RunWithVisibleTerminal("cmd.exe", $"/c rmdir /S /Q \"{sourceDir}\"")
                        : AdminHelper.RunAsAdminWithTerminal("cmd.exe", $"/c rmdir /S /Q \"{sourceDir}\"");
                    if (exitCode == 0 && !Directory.Exists(sourceDir)) return true;
                }
                catch { }
            }

            return !Directory.Exists(sourceDir);
        }

        private static bool TryDeleteDirect(string path)
        {
            try { Directory.Delete(path, recursive: true); return !Directory.Exists(path); }
            catch { return false; }
        }

        private static void CopyDirMeta(DirectoryInfo src, DirectoryInfo dst)
        {
            try
            {
                dst.CreationTime = src.CreationTime;
                dst.CreationTimeUtc = src.CreationTimeUtc;
                dst.LastWriteTime = src.LastWriteTime;
                dst.LastWriteTimeUtc = src.LastWriteTimeUtc;
                dst.Attributes = src.Attributes;
            }
            catch { /* 属性复制失败不阻断 */ }
        }

        private static void CopyFileMeta(FileInfo src, FileInfo dst)
        {
            try
            {
                dst.CreationTime = src.CreationTime;
                dst.CreationTimeUtc = src.CreationTimeUtc;
                dst.LastWriteTime = src.LastWriteTime;
                dst.LastWriteTimeUtc = src.LastWriteTimeUtc;
                dst.Attributes = src.Attributes;
            }
            catch { /* 属性复制失败不阻断 */ }
        }
    }
}