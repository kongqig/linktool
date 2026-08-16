using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

namespace linktool.Services
{
    /// <summary>
    /// 管理员权限及可见终端进程执行工具。
    /// </summary>
    public static class AdminHelper
    {
        /// <summary>当前进程是否以管理员身份运行</summary>
        public static bool IsRunningAsAdmin()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        /// <summary>通过 runas 提权重启当前应用</summary>
        public static void RestartAsAdmin()
        {
            try
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath)) return;
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(psi);
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                DialogHelper.Warn("错误", $"无法以管理员身份运行：{ex.Message}");
            }
        }

        /// <summary>以管理员权限调用可见终端执行命令，返回退出码</summary>
        public static int RunAsAdminWithTerminal(string fileName, string arguments)
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

        /// <summary>以当前权限调用可见终端执行命令，返回退出码</summary>
        public static int RunWithVisibleTerminal(string fileName, string arguments)
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
    }
}