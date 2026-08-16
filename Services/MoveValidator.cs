using System;
using System.IO;
using System.Linq;

namespace linktool.Services
{
    /// <summary>
    /// 移动/链接操作的安全校验。返回错误消息字符串，null 表示校验通过。
    /// </summary>
    public static class MoveValidator
    {
        /// <summary>校验单次迁移的 from/to 路径</summary>
        public static string? Validate(string fromPath, string toPath)
        {
            if (string.IsNullOrWhiteSpace(fromPath)) return "请输入或选择要迁移的文件夹路径。";
            if (string.IsNullOrWhiteSpace(toPath)) return "请输入或选择目标文件夹路径。";

            if (!IsValidPath(fromPath)) return $"源路径格式不合法：\n{fromPath}";
            if (!IsValidPath(toPath)) return $"目标路径格式不合法：\n{toPath}";

            if (!Directory.Exists(fromPath)) return $"源文件夹不存在：\n{fromPath}";

            if (JunctionHelper.IsReparsePoint(fromPath))
                return $"源文件夹是目录链接或符号链接，无法迁移：\n{fromPath}\n\n请选择真实的文件夹。";

            var fullFrom = Path.GetFullPath(fromPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullTo = Path.GetFullPath(toPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(fullFrom, fullTo, StringComparison.OrdinalIgnoreCase))
                return "源文件夹和目标文件夹不能相同。";

            if (fullTo.StartsWith(fullFrom + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return "目标文件夹不能是源文件夹的子目录，这会导致递归移动。";

            var fromRoot = Path.GetPathRoot(fromPath);
            var toRoot = Path.GetPathRoot(toPath);
            if (!string.IsNullOrEmpty(fromRoot) && !Directory.Exists(fromRoot)) return $"源路径所在的驱动器不存在：\n{fromRoot}";
            if (!string.IsNullOrEmpty(toRoot) && !Directory.Exists(toRoot)) return $"目标路径所在的驱动器不存在：\n{toRoot}";

            return null;
        }

        /// <summary>校验批量迁移中单行（from 必填，to 可回退通用目录）</summary>
        public static string? ValidateRow(string fromPath, string? toPath)
        {
            if (string.IsNullOrWhiteSpace(fromPath)) return "源路径不能为空。";
            if (!IsValidPath(fromPath)) return $"源路径格式不合法：\n{fromPath}";
            if (!Directory.Exists(fromPath)) return $"源文件夹不存在：\n{fromPath}";
            if (JunctionHelper.IsReparsePoint(fromPath)) return $"源已是目录链接/符号链接：\n{fromPath}";

            if (!string.IsNullOrWhiteSpace(toPath))
            {
                if (!IsValidPath(toPath)) return $"目标路径格式不合法：\n{toPath}";
                var toRoot = Path.GetPathRoot(toPath);
                if (!string.IsNullOrEmpty(toRoot) && !Directory.Exists(toRoot)) return $"目标驱动器不存在：\n{toRoot}";
            }
            return null;
        }

        /// <summary>校验路径格式是否合法</summary>
        public static bool IsValidPath(string path)
        {
            try
            {
                Path.GetFullPath(path);
                var fileName = Path.GetFileName(path);
                if (string.IsNullOrEmpty(fileName)) return true;
                var invalidChars = Path.GetInvalidFileNameChars();
                return fileName.Any(c => Array.IndexOf(invalidChars, c) >= 0) == false;
            }
            catch { return false; }
        }
    }
}