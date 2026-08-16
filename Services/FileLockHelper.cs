using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace linktool.Services
{
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
        /// 检查 from 下所有文件的访问权限，返回无权限文件列表（空=全部可访问）。
        /// 用 FileShare.ReadWrite|Delete 避免正常读取误报。
        /// </summary>
        public static List<string> CheckFileAccessibility(string fromPath)
        {
            var denied = new List<string>();
            try
            {
                foreach (var file in Directory.EnumerateFiles(fromPath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        denied.Add(file);
                    }
                    catch (IOException)
                    {
                        var errorCode = Marshal.GetHRForLastWin32Error();
                        // 共享/锁冲突不算不可访问
                        if (errorCode != unchecked((int)0x80070020) && errorCode != unchecked((int)0x80070021))
                            denied.Add(file);
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                denied.Add(fromPath + " (无法枚举目录内容)");
            }
            return denied;
        }

        /// <summary>
        /// 检查 from 下被独占锁定的文件列表（仅共享冲突 32/33 判定为锁定）。
        /// </summary>
        public static List<string> CheckFileLocks(string fromPath)
        {
            var locked = new List<string>();
            try
            {
                foreach (var file in Directory.EnumerateFiles(fromPath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException ex)
                    {
                        var hresult = ex.HResult & 0xFFFF;
                        if (hresult == 32 || hresult == 33)
                            locked.Add(file);
                    }
                }
            }
            catch (UnauthorizedAccessException) { }
            return locked;
        }
    }
}