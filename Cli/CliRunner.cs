using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace linktool.Cli
{
    /// <summary>
    /// 命令行入口：无 GUI，非交互执行迁移/链接/退迁/批量操作。
    /// 程序为控制台子系统，由终端启动时输出天然干净（无需 AttachConsole）。
    /// 返回退出码（0=成功）。
    /// </summary>
    public static class CliRunner
    {
        /// <summary>CLI 入口。返回退出码。</summary>
        public static int Run(string[] args)
        {
            try
            {
                if (args == null || args.Length == 0)
                {
                    PrintUsage();
                    return 0;
                }

                var cmd = args[0].ToLowerInvariant();
                switch (cmd)
                {
                    case "help":
                    case "--help":
                    case "-h":
                        PrintUsage();
                        return 0;
                    case "migrate":
                        return CmdMigrate(Rest(args));
                    case "link":
                        return CmdLink(Rest(args));
                    case "retreat":
                        return CmdRetreat(Rest(args));
                    case "batch-migrate":
                        return CmdBatchMigrate(Rest(args));
                    case "batch-link":
                        return CmdBatchLink(Rest(args));
                    default:
                        Console.WriteLine($"未知命令：{cmd}");
                        PrintUsage();
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"错误：{ex.Message}");
                return 1;
            }
        }

        #region 命令实现

        private static int CmdMigrate(string[] args)
        {
            var from = GetArg(args, "--from");
            var to = GetArg(args, "--to");
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
                return Err("migrate 需要 --from <源> 和 --to <目标目录>");

            var error = Services.MoveValidator.Validate(from, to);
            if (error != null) return Err(error);

            var name = Path.GetFileName(from.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var dest = Path.Combine(to, name);

            if (Directory.Exists(dest))
            {
                bool empty;
                try { empty = !Directory.EnumerateFileSystemEntries(dest).Any(); }
                catch { empty = false; }
                if (!empty) return Err($"目标已存在且非空：{dest}");
            }

            var locked = Services.FileLockHelper.CheckFileLocks(from);
            if (locked.Count > 0)
                return Err($"源文件夹被占用，请关闭占用程序后重试（{locked.First()} 等 {locked.Count} 项）");

            Console.WriteLine($"迁移：{from} → {dest}");

            var allFiles = Services.FileOps.GetAllFiles(from);
            if (allFiles.Count > 0)
            {
                using var locker = new Services.DirectoryLockManager();
                try { locker.Lock(dest); } catch { }
                Services.FileOps.CopyDirectoryFull(from, dest, allFiles, CancellationToken.None,
                    (c, t, f) => Console.WriteLine($"  复制 [{c}/{t}] {f}"));
            }
            else
            {
                Directory.CreateDirectory(dest);
            }

            if (!Services.FileOps.DeleteSourceDirectory(from, CancellationToken.None))
                Console.WriteLine("  警告：源文件夹删除失败，请手动清理");

            // 链接创建失败时，文件已迁移、源已删除，不再返回失败码，仅给出手动修复提示
            try
            {
                Services.JunctionHelper.CreateJunction(from, dest);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"警告：迁移完成，但目录链接创建失败：{ex.Message}");
                Console.WriteLine($"请手动执行：mklink /J \"{from}\" \"{dest}\"");
                return 0;
            }
            Console.WriteLine($"完成：已创建链接 {from} → {dest}");
            return 0;
        }

        private static int CmdLink(string[] args)
        {
            var linkDir = GetArg(args, "--link");
            var target = GetArg(args, "--target");
            if (string.IsNullOrWhiteSpace(linkDir) || string.IsNullOrWhiteSpace(target))
                return Err("link 需要 --link <链接目录> --target <目标路径>");

            if (!Directory.Exists(target)) return Err($"目标路径不存在：{target}");
            if (Services.JunctionHelper.IsReparsePoint(target)) return Err($"目标路径已是目录链接：{target}");

            var name = Path.GetFileName(target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var junction = Path.Combine(linkDir, name);
            Services.JunctionHelper.CreateJunction(junction, target);
            Console.WriteLine($"完成：已创建链接 {junction} → {target}");
            return 0;
        }

        private static int CmdRetreat(string[] args)
        {
            var link = GetArg(args, "--link");
            if (string.IsNullOrWhiteSpace(link)) return Err("retreat 需要 --link <链接路径>");

            if (!Directory.Exists(link)) return Err($"链接路径不存在：{link}");
            if (!Services.JunctionHelper.IsReparsePoint(link)) return Err($"该路径不是目录链接：{link}");

            var target = Services.RetreatHelper.ResolveTarget(link);
            Console.WriteLine($"退迁：{link} → {target}");
            Services.RetreatHelper.Retreat(link,
                (c, t, f) => Console.WriteLine($"  复制 [{c}/{t}] {f}"));
            Console.WriteLine("完成：已退迁");
            return 0;
        }

        private static int CmdBatchMigrate(string[] args)
        {
            var fromList = Split(GetArg(args, "--from"));
            var to = GetArg(args, "--to");
            if (fromList.Count == 0 || string.IsNullOrWhiteSpace(to))
                return Err("batch-migrate 需要 --from <源1;源2;...> --to <目标目录>");

            var failed = 0;
            foreach (var from in fromList)
            {
                var code = CmdMigrate(new[] { "migrate", "--from", from, "--to", to });
                if (code != 0) failed++;
            }
            return failed == 0 ? 0 : 1;
        }

        private static int CmdBatchLink(string[] args)
        {
            var targetList = Split(GetArg(args, "--target"));
            var linkDir = GetArg(args, "--link-dir");
            if (targetList.Count == 0 || string.IsNullOrWhiteSpace(linkDir))
                return Err("batch-link 需要 --target <目标1;目标2;...> --link-dir <通用链接目录>");

            var failed = 0;
            foreach (var target in targetList)
            {
                var code = CmdLink(new[] { "link", "--link", linkDir, "--target", target });
                if (code != 0) failed++;
            }
            return failed == 0 ? 0 : 1;
        }

        #endregion

        #region 辅助

        private static string[] Rest(string[] args) => args.Skip(1).ToArray();

        private static string? GetArg(string[] args, string key)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
        }

        private static List<string> Split(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return new List<string>();
            return s.Split(new[] { ';', '、', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        }

        private static int Err(string message)
        {
            Console.WriteLine($"错误：{message}");
            return 1;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("LinkTool 命令行工具");
            Console.WriteLine("");
            Console.WriteLine("用法：");
            Console.WriteLine("  linktool <命令> [选项]");
            Console.WriteLine("");
            Console.WriteLine("命令：");
            Console.WriteLine("  migrate       --from <源> --to <目标目录>        单个迁移（复制→删源→建链接）");
            Console.WriteLine("  link          --link <链接目录> --target <目标>   创建目录链接（链接目录\\目标同名）");
            Console.WriteLine("  retreat       --link <链接路径>                 退迁（把链接目标内容移回原路径）");
            Console.WriteLine("  batch-migrate --from <源1;源2> --to <目标目录>    批量迁移");
            Console.WriteLine("  batch-link    --target <目标1;目标2> --link-dir <通用链接目录>  批量链接");
            Console.WriteLine("  help                                        显示帮助");
            Console.WriteLine("");
            Console.WriteLine("退出码：0=成功，非0=失败。");
        }

        #endregion
    }
}