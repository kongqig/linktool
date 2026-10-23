using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace linktool.Services
{
    /// <summary>文件夹大小计算结果</summary>
    public sealed record FolderSizeResult(long TotalBytes, int FileCount, bool Incomplete);

    /// <summary>
    /// 文件夹大小 / 磁盘可用空间计算。后台遍历、异常跳过、可取消。
    /// </summary>
    public static class SizeHelper
    {
        /// <summary>
        /// 执行时空间检查的安全余量：需要 = 源大小 + 余量。
        /// 预留给文件系统元数据、写入缓存等，保证磁盘仍能正常读写。
        /// </summary>
        public const long SpaceMarginBytes = 1024L * 1024 * 1024; // 1 GB

        /// <summary>后台计算文件夹总大小（出错跳过，Incomplete 标记统计不完整）</summary>
        public static Task<FolderSizeResult> GetSizeAsync(string path, CancellationToken ct)
            => Task.Run(() => GetSize(path, ct), ct);

        /// <summary>同步计算文件夹总大小（调用方自行放到后台线程）</summary>
        public static FolderSizeResult GetSize(string path, CancellationToken ct)
        {
            long bytes = 0;
            var count = 0;
            var incomplete = false;
            try
            {
                // 跳过 ReparsePoint：不跟随源内部链接（防循环递归、防统计到链接目标内容）
                foreach (var file in Directory.EnumerateFiles(path, "*", FileLockHelper.EnumerationOptionsSkipReparse))
                {
                    ct.ThrowIfCancellationRequested();
                    count++;
                    try { bytes += new FileInfo(file).Length; }
                    catch { incomplete = true; }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { incomplete = true; }
            return new FolderSizeResult(bytes, count, incomplete);
        }

        /// <summary>获取路径所在驱动器的可用字节数，无法判断时返回 null</summary>
        public static long? GetFreeSpace(string path)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root)) return null;
                var drive = new DriveInfo(root);
                if (!drive.IsReady) return null;
                return drive.AvailableFreeSpace;
            }
            catch { return null; }
        }

        /// <summary>格式化字节数为可读文本（B/KB/MB/GB/TB）</summary>
        public static string Format(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes;
            var u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return $"{v:0.##} {units[u]}";
        }
    }
}
