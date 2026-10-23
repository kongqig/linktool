using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
namespace linktool.Services
{
    /// <summary>Junction 创建失败专用异常（用于区分“复制/删除已完成，仅链接失败”）</summary>
    public sealed class JunctionHelperJunctionException : Exception
    {
        public JunctionHelperJunctionException(string message) : base(message) { }
    }

    /// <summary>
    /// Junction（目录联接）创建与检测。降级策略：Win32 API → 管理员重试 → cmd mklink /J。
    /// </summary>
    public static class JunctionHelper
    {
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        private const int FSCTL_SET_REPARSE_POINT = 0x000900A4;
        private const int FSCTL_GET_REPARSE_POINT = 0x000900A8;
        private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
        private const uint IO_REPARSE_TAG_SYMLINK = 0xA000000C;
        private const uint FILE_SHARE_ALL = 0x7; // READ | WRITE | DELETE

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
        private static extern bool DeviceIoControl(
            IntPtr hDevice, uint dwIoControlCode,
            IntPtr lpInBuffer, uint nInBufferSize,
            [Out] byte[] lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        /// <summary>重叠校验用归一化：全路径、去尾部分隔符。失败时退回原始 Trim</summary>
        private static string NormalizeForOverlap(string path)
        {
            try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch { return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        }

        /// <summary>判断路径是否为 Junction 或符号链接</summary>
        public static bool IsReparsePoint(string path)
        {
            try
            {
                return new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);
            }
            catch { return false; }
        }

        /// <summary>
        /// 直接读取 reparse point 原始数据并解析目标（FSCTL_GET_REPARSE_POINT）。
        /// 用于 .NET LinkTarget 解析不出目标（返回空/抛异常）的特殊链接兜底。
        /// 返回目标路径（仅 Junction/符号链接可解析）；读取失败或非常规标签返回 null，tagCode 返回 reparse 标签值。
        /// </summary>
        public static string? ReadReparseTargetRaw(string path, out uint tagCode)
        {
            tagCode = 0;
            IntPtr handle;
            try
            {
                handle = CreateFile(path, 0, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
                    FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            }
            catch { return null; }
            if (handle == new IntPtr(-1)) return null;

            try
            {
                var buffer = new byte[16 * 1024]; // MAXIMUM_REPARSE_DATA_BUFFER_SIZE
                if (!DeviceIoControl(handle, (uint)FSCTL_GET_REPARSE_POINT, IntPtr.Zero, 0,
                    buffer, (uint)buffer.Length, out var bytesReturned, IntPtr.Zero))
                    return null;
                if (bytesReturned < 8) return null;

                tagCode = BitConverter.ToUInt32(buffer, 0);
                // 布局：ReparseTag(4) + ReparseDataLength(2) + Reserved(2)，之后为各标签自己的缓冲区，
                // 其 PathBuffer 相对偏移为 SubstituteNameOffset/PrintNameOffset 的基准
                if (tagCode == IO_REPARSE_TAG_MOUNT_POINT)
                {
                    var subOffset = BitConverter.ToUInt16(buffer, 8);
                    var subLength = BitConverter.ToUInt16(buffer, 10);
                    var s = Encoding.Unicode.GetString(buffer, 16 + subOffset, subLength);
                    return CleanReparsePath(s);
                }
                if (tagCode == IO_REPARSE_TAG_SYMLINK)
                {
                    var subOffset = BitConverter.ToUInt16(buffer, 8);
                    var subLength = BitConverter.ToUInt16(buffer, 10);
                    var s = Encoding.Unicode.GetString(buffer, 20 + subOffset, subLength);
                    return CleanReparsePath(s);
                }
                return null; // 其他标签（CLOUD/APPEXECLINK 等）无法给出目录目标
            }
            catch { return null; }
            finally { CloseHandle(handle); }
        }

        /// <summary>清理 reparse 原始路径：去掉 \??\ / \\?\ 前缀与结尾空字符</summary>
        private static string CleanReparsePath(string s)
        {
            s = s.TrimEnd('\0');
            if (s.StartsWith(@"\??\", StringComparison.Ordinal)) s = s[4..];
            else if (s.StartsWith(@"\\?\", StringComparison.Ordinal)) s = s[4..];
            return s;
        }

        /// <summary>
        /// 创建 Junction 链接（多级降级）。失败时抛出异常。
        /// 与原始实现一致：Win32 API → 重试 → cmd mklink /J。
        /// 前置防御：拒绝 UNC 目标；拒绝链接与目标重叠（相同/互为祖先，否则会形成循环链接）。
        /// </summary>
        public static void CreateJunction(string junctionPath, string targetPath)
        {
            // Junction 不支持网络（UNC）目标
            var rawTarget = targetPath.Trim();
            if (rawTarget.StartsWith(@"\\") || rawTarget.StartsWith("//"))
                throw new JunctionHelperJunctionException($"Junction 目标不支持网络路径（UNC）：\n{targetPath}");

            // 重叠校验：链接与目标相同或互为祖先时，访问链接会无限重定向/枚举成环
            var link = NormalizeForOverlap(junctionPath);
            var target = NormalizeForOverlap(targetPath);
            if (string.Equals(link, target, StringComparison.OrdinalIgnoreCase))
                throw new JunctionHelperJunctionException($"链接位置与目标路径相同，会形成自指链接：\n{junctionPath}");
            if (target.StartsWith(link + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new JunctionHelperJunctionException($"目标路径位于链接位置内部，会形成循环链接：\n链接：{junctionPath}\n目标：{targetPath}");
            if (link.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new JunctionHelperJunctionException($"链接位置位于目标路径内部，会形成循环链接：\n链接：{junctionPath}\n目标：{targetPath}");

            // 若链接位置已存在且非空，无法创建
            if (Directory.Exists(junctionPath))
            {
                if (Directory.EnumerateFileSystemEntries(junctionPath).Any())
                    throw new JunctionHelperJunctionException($"链接位置已存在且非空：{junctionPath}");
                Directory.Delete(junctionPath, false);
            }

            // 创建空目录作为 Junction 载体
            Directory.CreateDirectory(junctionPath);

            // 第一步：尝试 Win32 API
            if (TryNative(junctionPath, targetPath)) return;

            // Win32 失败，清理后重建空目录再重试
            ResetEmpty(junctionPath);

            // 第二步：非管理员时重试 Win32 API
            if (!AdminHelper.IsRunningAsAdmin() && TryNative(junctionPath, targetPath)) return;

            // 第三步：cmd mklink /J（注意：mklink 要求链接路径不存在，因此只删除、不重建）
            try { Directory.Delete(junctionPath, false); } catch { }
            try
            {
                var exitCode = AdminHelper.IsRunningAsAdmin()
                    ? AdminHelper.RunWithVisibleTerminal("cmd.exe", $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"")
                    : AdminHelper.RunAsAdminWithTerminal("cmd.exe", $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"");
                if (exitCode == 0 && IsReparsePoint(junctionPath)) return;
            }
            catch { }

            throw new JunctionHelperJunctionException($"目录链接创建失败，所有方式均未成功。\n链接路径：{junctionPath}\n目标路径：{targetPath}");
        }

        private static bool TryNative(string junctionPath, string targetPath)
        {
            try
            {
                CreateJunctionNative(junctionPath, targetPath);
                return IsReparsePoint(junctionPath);
            }
            catch { return false; }
        }

        /// <summary>清理并重建空目录（用于 Win32 重试前）</summary>
        private static void ResetEmpty(string junctionPath)
        {
            try { Directory.Delete(junctionPath, false); } catch { }
            try { Directory.CreateDirectory(junctionPath); } catch { }
        }

        /// <summary>通过 Win32 FSCTL_SET_REPARSE_POINT 创建 Junction</summary>
        private static void CreateJunctionNative(string junctionPath, string targetPath)
        {
            var targetPathFormatted = @"\??\" + targetPath.TrimEnd('\\') + "\\\0";
            var targetBytes = Encoding.Unicode.GetBytes(targetPathFormatted);

            var headerSize = 8 + 8;
            var fullData = new byte[headerSize + targetBytes.Length + 2];

            BitConverter.GetBytes(IO_REPARSE_TAG_MOUNT_POINT).CopyTo(fullData, 0);
            BitConverter.GetBytes((ushort)(8 + targetBytes.Length + 2)).CopyTo(fullData, 4);
            BitConverter.GetBytes((ushort)0).CopyTo(fullData, 6);

            BitConverter.GetBytes((ushort)0).CopyTo(fullData, 8);
            BitConverter.GetBytes((ushort)targetBytes.Length).CopyTo(fullData, 10);
            BitConverter.GetBytes((ushort)targetBytes.Length).CopyTo(fullData, 12);
            BitConverter.GetBytes((ushort)0).CopyTo(fullData, 14);

            targetBytes.CopyTo(fullData, 16);

            var handle = CreateFile(
                junctionPath, GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING,
                FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);

            if (handle == new IntPtr(-1))
                Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());

            try
            {
                if (!DeviceIoControl(handle, (uint)FSCTL_SET_REPARSE_POINT, fullData, (uint)fullData.Length,
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
    }
}