using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using linktool.Dialogs;

namespace linktool.Services
{
    /// <summary>
    /// 交互式前置检查辅助：占用进程处理闭环 + 磁盘空间检查（含免责确认）。
    /// 供单页（迁移/链接/退迁）使用；批量页保持静默，直接标失败。
    /// </summary>
    public static class PreCheckHelper
    {
        /// <summary>
        /// 占用处理闭环：弹出进程列表供用户强制结束，结束后自动复检；
        /// 复检通过返回 true，用户关闭弹窗放弃返回 false（调用方应终止操作）。
        /// checkLocks 返回当前仍占用路径的进程列表（在后台线程执行）。
        /// </summary>
        public static async Task<bool> ResolveLocksAsync(string title, string description, Func<List<Process>> checkLocks)
        {
            while (true)
            {
                var procs = await Task.Run(checkLocks);
                if (procs.Count == 0) return true;
                var dlg = new ProcessListDialog(title, description, procs);
                if (dlg.ShowDialog() != true) return false; // 关闭弹窗 = 放弃并终止
                // 用户点击了强制结束 → 循环复检
            }
        }

        /// <summary>
        /// 综合占用检测：文件级锁定扫描 + Restart Manager / 模块枚举进程定位。
        /// 返回占用进程列表；无文件锁定时返回空列表。
        /// </summary>
        public static List<Process> DetectLockProcesses(string path)
        {
            var scan = FileLockHelper.ScanDirectory(path);
            if (scan.Locked.Count == 0) return new List<Process>();
            var procs = FileLockHelper.GetLockingProcesses(path);
            return procs.Count > 0 ? procs : FileLockHelper.GetProcessesLockingDirectory(path);
        }

        /// <summary>
        /// 空间检查（交互式）：需要 = 内容大小 + 安全余量(1GB)。
        /// 不足时弹免责弹窗，由用户决定是否强行执行。
        /// 返回 true=继续执行（足够或用户自担风险），false=取消。
        /// </summary>
        public static bool CheckSpaceInteractive(long contentBytes, string destPath, string destDesc)
        {
            var free = SizeHelper.GetFreeSpace(destPath);
            if (free == null) return true; // 无法判断时放行
            var required = contentBytes + SizeHelper.SpaceMarginBytes;
            if (free.Value >= required) return true;

            var margin = SizeHelper.Format(SizeHelper.SpaceMarginBytes);
            var msg = $"目标位置所在磁盘可用空间不足：\n\n{destDesc}\n\n" +
                      $"需要：{SizeHelper.Format(required)}（含 {margin} 安全余量）\n" +
                      $"可用：{SizeHelper.Format(free.Value)}\n" +
                      $"缺少：{SizeHelper.Format(required - free.Value)}\n\n" +
                      "警告：空间不足时强行执行可能导致复制不完整、数据损坏或系统运行不稳定，" +
                      "由此产生的一切数据丢失与后果由用户自行承担，本程序作者不承担任何责任。\n\n" +
                      "是否仍要执行？";
            return DialogHelper.Confirm("磁盘空间不足", msg, "仍要执行（自担风险）", danger: true);
        }
    }
}
