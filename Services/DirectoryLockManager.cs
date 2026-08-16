using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace linktool.Services
{
    /// <summary>
    /// 目录独占锁定管理器：通过 CreateFile 持有目录句柄，防止其他进程写入。
    /// </summary>
    public sealed class DirectoryLockManager : IDisposable
    {
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private readonly List<IntPtr> _handles = new();

        /// <summary>独占锁定目录（失败静默忽略）</summary>
        public void Lock(string path)
        {
            var handle = CreateFile(path, GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (handle != new IntPtr(-1))
                _handles.Add(handle);
        }

        /// <summary>释放所有锁定</summary>
        public void UnlockAll()
        {
            foreach (var h in _handles)
                CloseHandle(h);
            _handles.Clear();
        }

        public void Dispose() => UnlockAll();
    }
}