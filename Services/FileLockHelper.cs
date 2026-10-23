using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace linktool.Services
{
    /// <summary>目录综合扫描结果：权限、占用、大小、链接子目录（一次遍历产出）</summary>
    public sealed record ScanResult(List<string> Denied, List<string> Locked, long TotalBytes, int FileCount, List<string> ReparseDirs);

    /// <summary>
    /// 文件占用/访问权限检测。
    /// 含 Restart Manager 精确占用检测与进程模块枚举降级检测。
    /// </summary>
    public static class FileLockHelper
    {
        #region Restart Manager

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

        #endregion

        /// <summary>获取占用指定路径的进程列表（Restart Manager）</summary>
        public static List<Process> GetLockingProcesses(string path)
        {
            var processes = new List<Process>();
            int res = RmStartSession(out uint handle, 0, Guid.NewGuid().ToString());
            if (res != 0) return processes;
            try
            {
                var resources = new[] { path };
                res = RmRegisterResources(handle, (uint)resources.Length, resources, 0, null!, 0, null!);
                if (res != 0) return processes;

                uint pnProcInfo = 0;
                uint lpdwRebootReasons = 0;
                res = RmGetList(handle, out uint pnProcInfoNeeded, ref pnProcInfo, null!, ref lpdwRebootReasons);
                if (res != 0 && pnProcInfoNeeded == 0) return processes;

                var processInfos = new RM_PROCESS_INFO[pnProcInfoNeeded];
                pnProcInfo = pnProcInfoNeeded;
                res = RmGetList(handle, out pnProcInfoNeeded, ref pnProcInfo, processInfos, ref lpdwRebootReasons);
                if (res != 0) return processes;

                for (int i = 0; i < pnProcInfo; i++)
                {
                    try
                    {
                        processes.Add(Process.GetProcessById(processInfos[i].Process.dwProcessId));
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

        /// <summary>获取工作目录/模块位于目标目录下的进程（降级检测）</summary>
        public static List<Process> GetProcessesLockingDirectory(string directoryPath)
        {
            var lockingProcesses = new List<Process>();
            var normalized = Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
            try
            {
                foreach (var process in Process.GetProcesses())
                {
                    bool added = false;
                    try
                    {
                        if (process.MainModule?.FileName != null &&
                            Path.GetFullPath(process.MainModule.FileName).StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        {
                            lockingProcesses.Add(process);
                            added = true;
                        }
                    }
                    catch { }

                    if (added) continue;

                    try
                    {
                        foreach (ProcessModule module in process.Modules)
                        {
                            try
                            {
                                if (module.FileName != null &&
                                    Path.GetFullPath(module.FileName).StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                                {
                                    lockingProcesses.Add(process);
                                    break;
                                }
                            }
                            catch { break; }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return lockingProcesses;
        }

        /// <summary>
        /// 综合扫描目录：访问权限、独占锁定、总大小、文件数、内部链接子目录。
        /// 枚举递归时跳过 ReparsePoint 条目——源内部的 Junction/符号链接不会被跟随，
        /// 既不会把链接目标的内容统计/复制进来，也不会因循环链接（如 link → 祖先目录）陷入无限递归。
        /// 权限不足( UnauthorizedAccessException )记入 Denied，
        /// 共享冲突(32/33) 记入 Locked，其余 IO 错误记入 Denied。
        /// </summary>
        public static ScanResult ScanDirectory(string path)
        {
            var denied = new List<string>();
            var locked = new List<string>();
            var reparseDirs = FindReparseSubDirs(path);
            long bytes = 0;
            var count = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", EnumerationOptionsSkipReparse))
                {
                    count++;
                    try { bytes += new FileInfo(file).Length; } catch { }
                    try
                    {
                        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        denied.Add(file);
                    }
                    catch (IOException ex)
                    {
                        var hresult = ex.HResult & 0xFFFF;
                        if (hresult == 32 || hresult == 33)
                            locked.Add(file);
                        else
                            denied.Add(file);
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                denied.Add(path + " (无法枚举目录内容)");
            }
            return new ScanResult(denied, locked, bytes, count, reparseDirs);
        }

        /// <summary>递归枚举统一选项：跟随子目录，但跳过 ReparsePoint（防循环、防跟出源外）</summary>
        internal static EnumerationOptions EnumerationOptionsSkipReparse => new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        /// <summary>
        /// 环安全地找出目录下所有链接子目录（不递归进入链接本身）。
        /// </summary>
        public static List<string> FindReparseSubDirs(string rootPath)
        {
            var result = new List<string>();
            var stack = new Stack<string>();
            stack.Push(rootPath);
            while (stack.Count > 0)
            {
                string dir;
                try { dir = stack.Pop(); } catch { continue; }
                IEnumerable<string> children;
                try { children = Directory.EnumerateDirectories(dir); }
                catch { continue; }
                foreach (var child in children)
                {
                    try
                    {
                        if (new DirectoryInfo(child).Attributes.HasFlag(FileAttributes.ReparsePoint))
                            result.Add(child); // 链接本身记录，但不入栈（不跟随，天然防环）
                        else
                            stack.Push(child);
                    }
                    catch { }
                }
            }
            return result;
        }
    }
}