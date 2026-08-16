using System;
using System.IO;
using System.Threading;

namespace linktool.Services
{
    /// <summary>
    /// 退迁（反向迁移）：把已迁移成 Junction 的目录内容安全移回原路径，并删除链接。
    /// 迁移：L(链接) → T(目标)。退迁：把 T 的内容移回 L，删除 L 的链接与 T。
    /// </summary>
    public static class RetreatHelper
    {
        /// <summary>
        /// 解析 Junction 链接指向的目标路径。失败抛异常。
        /// </summary>
        public static string ResolveTarget(string junctionPath)
        {
            var di = new DirectoryInfo(junctionPath);
            var t = di.LinkTarget;
            if (string.IsNullOrEmpty(t))
                throw new InvalidOperationException("无法解析链接目标，可能不是有效的目录链接。");
            if (t.StartsWith(@"\??\", StringComparison.Ordinal))
                t = t.Substring(4);
            if (Path.IsPathRooted(t))
                return Path.GetFullPath(t);
            var parent = di.Parent?.FullName;
            if (string.IsNullOrEmpty(parent))
                throw new InvalidOperationException("无法解析链接目标（缺少父目录）。");
            return Path.GetFullPath(Path.Combine(parent, t));
        }

        /// <summary>
        /// 退迁单个链接。安全策略：先把目标内容复制到临时目录，再删除链接、移动回原路径、删除旧目标。
        /// 任一步失败都不会破坏原链接。
        /// </summary>
        public static void Retreat(string junctionPath, ProgressHandler? progress = null)
        {
            if (!JunctionHelper.IsReparsePoint(junctionPath))
                throw new InvalidOperationException($"路径不是目录链接：{junctionPath}");

            var targetPath = ResolveTarget(junctionPath);
            if (!Directory.Exists(targetPath))
                throw new InvalidOperationException($"链接目标不存在：{targetPath}");
            if (JunctionHelper.IsReparsePoint(targetPath))
                throw new InvalidOperationException("链接目标仍是目录链接，请先处理目标后再退迁。");

            var parent = Path.GetDirectoryName(junctionPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                         ?? Path.GetTempPath();
            var tempDir = Path.Combine(parent, ".linktool_retreat_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                // 1. 将目标内容复制到临时目录（同一卷，便于后续移动）
                var allFiles = FileOps.GetAllFiles(targetPath);
                if (allFiles.Count > 0)
                    FileOps.CopyDirectoryFull(targetPath, tempDir, allFiles, CancellationToken.None, progress);

                // 2. 删除原链接（仅重解析点，不影响目标）；带重试以规避瞬时占用
                DeleteJunctionWithRetry(junctionPath);

                // 3. 临时目录改名为原路径；带重试
                if (Directory.Exists(tempDir))
                    MoveWithRetry(tempDir, junctionPath);

                // 4. 删除旧目标
                try { Directory.Delete(targetPath, true); } catch { }
            }
            catch
            {
                // 回滚：清理临时目录，尽量保留原链接可用
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
                throw;
            }
        }

        /// <summary>删除 Junction 重解析点，带多次重试以规避瞬时占用/延迟</summary>
        private static void DeleteJunctionWithRetry(string junctionPath)
        {
            for (var i = 0; i < 5; i++)
            {
                try
                {
                    Directory.Delete(junctionPath, false);
                    if (!Directory.Exists(junctionPath)) return;
                }
                catch { /* 瞬时失败，重试 */ }
                Thread.Sleep(200);
            }
            throw new InvalidOperationException($"删除目录链接失败：{junctionPath}");
        }

        /// <summary>目录重命名，带多次重试以规避瞬时“目标已存在”等</summary>
        private static void MoveWithRetry(string sourceDir, string destDir)
        {
            for (var i = 0; i < 5; i++)
            {
                try
                {
                    Directory.Move(sourceDir, destDir);
                    if (Directory.Exists(destDir)) return;
                }
                catch { /* 瞬时失败，重试 */ }
                Thread.Sleep(200);
            }
            throw new InvalidOperationException($"移动目录失败：{sourceDir} → {destDir}");
        }
    }
}