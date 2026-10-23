using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using linktool.Dialogs;
using linktool.Services;

namespace linktool.Pages
{
    /// <summary>
    /// 链接扫描页：递归扫描目录下的 Junction/符号链接，列出链接本体与目标位置。
    /// 默认遇到链接只记录不进入；勾选“启用链接递归”后跟随链接继续扫描（受递归限制约束）。
    /// </summary>
    public partial class LinkScanPage : UserControl
    {
        private const string ScanKey = "scan.path"; // 扫描路径历史键

        private readonly ObservableCollection<ScanRow> _rows = new();
        private readonly object _rowsLock = new();
        private CancellationTokenSource? _cts; // 非空表示扫描进行中

        public LinkScanPage()
        {
            InitializeComponent();
            PathHistoryHelper.Load(PathBox, ScanKey);
            Grid.ItemsSource = _rows;
            // 后台线程直接向集合添加行，UI 增量刷新
            BindingOperations.EnableCollectionSynchronization(_rows, _rowsLock);
        }

        #region 行模型

        /// <summary>单条链接记录（添加后只读，无需属性通知）</summary>
        public sealed class ScanRow
        {
            public string LinkPath { get; }
            public string Target { get; }
            public int Depth { get; }

            public ScanRow(string linkPath, string target, int depth)
            {
                LinkPath = linkPath;
                Target = target;
                Depth = depth;
            }
        }

        #endregion

        private void Path_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && sender is ComboBox cb)
                PathHistoryHelper.Remember(cb, ScanKey);
        }

        /// <summary>选择：从文件夹对话框选取扫描目录（编辑框为空时打开上次选择的目录）</summary>
        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var path = PathHistoryHelper.Browse("选择要扫描的目录", PathBox, ScanKey);
            if (path != null)
            {
                PathBox.Text = path;
                PathHistoryHelper.Remember(PathBox, ScanKey);
            }
        }

        private void ScanButton_Click(object sender, RoutedEventArgs e) => StartScan();

        private void StartScan()
        {
            if (_cts != null) return; // 扫描进行中

            var root = PathBox.Text?.Trim() ?? "";
            if (!Directory.Exists(root))
            {
                DialogHelper.Warn("提示", "请先填写有效的扫描路径。");
                return;
            }
            PathHistoryHelper.Remember(PathBox, ScanKey);

            // 递归限制：非法输入直接报错，不开始扫描
            if (!int.TryParse(LimitBox.Text?.Trim(), out var limit))
            {
                DialogHelper.Error("错误", "递归限制必须是整数（0 = 禁用链接递归）。");
                return;
            }
            if (limit < 0)
            {
                DialogHelper.Error("错误", "递归限制不能为负数（0 = 禁用链接递归）。");
                return;
            }
            if (limit > 100)
            {
                DialogHelper.Error("错误", "递归限制最大为 100 层，请调小后重试。");
                return;
            }

            // 遍历深度：目录层级上限（0=不限制），与链接递归限制互不影响
            if (!int.TryParse(DirLimitBox.Text?.Trim(), out var dirLimit))
            {
                DialogHelper.Error("错误", "遍历深度必须是整数（0 = 不限制）。");
                return;
            }
            if (dirLimit < 0)
            {
                DialogHelper.Error("错误", "遍历深度不能为负数（0 = 不限制）。");
                return;
            }
            if (dirLimit > 100)
            {
                DialogHelper.Error("错误", "遍历深度最大为 100 层，请调小后重试。");
                return;
            }

            var follow = FollowLinksCheck.IsChecked == true && limit > 0;
            var showHidden = ShowHiddenCheck.IsChecked == true;

            _rows.Clear();
            StatusText.Visibility = Visibility.Collapsed;
            SetBusy(true);
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            Task.Run(() =>
            {
                try
                {
                    var stat = ScanCore(root, follow, limit, dirLimit, showHidden, ct);
                    if (ct.IsCancellationRequested) return;

                    var text = $"扫描完成：共 {stat.Found} 个链接";
                    if (stat.DirLimited > 0)
                        text += $"；{stat.DirLimited} 个目录因超过遍历深度（{dirLimit} 层）未展开";
                    if (stat.Limited > 0)
                        text += $"；{stat.Limited} 条链接链达到递归限制（{limit} 层），未继续深入";
                    var kind = DialogKind.Info;
                    if (stat.MaxDepth > 10)
                    {
                        kind = DialogKind.Warning;
                        text += $"\n注意：存在超过 10 层的链接链（最深 {stat.MaxDepth} 层），可能存在循环链接，请谨慎处理。";
                    }
                    Dispatcher.Invoke(() => ShowStatus(text, kind));
                }
                catch (OperationCanceledException)
                {
                    // 扫描被取消，无需处理
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => ShowStatus($"扫描失败：{ex.Message}", DialogKind.Error));
                }
                finally
                {
                    Dispatcher.Invoke(SetBusyDone);
                }
            }, ct);
        }

        /// <summary>后台扫描核心：栈式遍历，遇到链接记录；是否跟随由 follow/limit 决定。
        /// 同一链接只记录一次（回环路径下不产生重复行），链仍继续遍历。
        /// dirDepth=当前目录层级（链接跳转后归零），超过 dirLimit 的普通子目录不展开；linkDepth=路径上已跟随的链接数。
        /// showHidden=false 时跳过隐藏/系统目录（如「开始」菜单、Application Data 等系统兼容链接）</summary>
        private ScanStat ScanCore(string root, bool follow, int limit, int dirLimit, bool showHidden, CancellationToken ct)
        {
            var stat = new ScanStat();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<(string Path, int DirDepth, int LinkDepth)>();
            stack.Push((root, 0, 0));

            // 枚举不做属性过滤（AttributesToSkip 会把属性读取失败的悬空/损坏条目一并滤掉），
            // 隐藏/系统/链接全部由下方手动判断
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = 0
            };

            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (dir, dirDepth, linkDepth) = stack.Pop();
                stat.Dirs++;
                if (stat.Dirs % 200 == 0)
                    Dispatcher.Invoke(() => StatusText.Text = $"扫描中… 已遍历 {stat.Dirs} 个目录，发现 {stat.Found} 个链接");

                IEnumerable<string> children;
                try { children = Directory.EnumerateDirectories(dir, "*", options); }
                catch { continue; }

                foreach (var child in children)
                {
                    FileAttributes attr;
                    try { attr = new DirectoryInfo(child).Attributes; }
                    catch { continue; }

                    // 属性为 -1（全 1）：条目本体无法读取——典型为悬空/损坏的链接（如应用残留死链）
                    if ((int)attr == -1)
                    {
                        stat.Found++;
                        stat.MaxDepth = Math.Max(stat.MaxDepth, linkDepth + 1);
                        if (seen.Add(child))
                            _rows.Add(new ScanRow(child, "（无法读取条目信息，可能为悬空或损坏的链接）", linkDepth + 1));
                        continue; // 无法访问，不跟随
                    }

                    // 默认跳过隐藏/系统目录（如「开始」菜单、Application Data 等系统兼容链接）
                    if (!showHidden && (attr.HasFlag(FileAttributes.Hidden) || attr.HasFlag(FileAttributes.System)))
                        continue;

                    if (!attr.HasFlag(FileAttributes.ReparsePoint))
                    {
                        // 普通子目录：目录层级 +1，超过遍历深度则不展开
                        var childDirDepth = dirDepth + 1;
                        if (dirLimit > 0 && childDirDepth > dirLimit)
                        {
                            stat.DirLimited++;
                            continue;
                        }
                        stack.Push((child, childDirDepth, linkDepth));
                        continue;
                    }

                    // 链接条目：记录本体与目标（去重：回环路径下同一链接只记录一次）
                    var linkDepthChild = linkDepth + 1;
                    if (linkDepthChild > stat.MaxDepth) stat.MaxDepth = linkDepthChild;
                    if (seen.Add(child))
                    {
                        stat.Found++;
                        var target = ResolveTargetSafe(child);
                        _rows.Add(new ScanRow(child, target, linkDepthChild));
                    }

                    // 是否跟随：受“启用链接递归 + 递归限制”约束；跟随后的目录层级从 0 重新计算
                    if (follow && linkDepthChild <= limit)
                    {
                        stack.Push((child, 0, linkDepthChild));
                    }
                    else if (follow)
                    {
                        stat.Limited++;
                    }
                }
            }
            return stat;
        }

        /// <summary>解析链接目标，并附加状态标记（自指/不存在/无访问权限）。
        /// .NET LinkTarget 解析不出时，回退为原始 reparse 数据直读（JunctionHelper.ReadReparseTargetRaw）</summary>
        private static string ResolveTargetSafe(string linkPath)
        {
            string? target;
            uint tag = 0;
            try
            {
                target = RetreatHelper.ResolveTarget(linkPath);
            }
            catch
            {
                target = JunctionHelper.ReadReparseTargetRaw(linkPath, out tag);
                if (target == null)
                    return tag != 0
                        ? $"（非常规链接类型 0x{tag:X8}，无法解析目标）"
                        : "（系统特殊链接，无法解析目标）";
            }

            string Norm(string p)
            {
                try { return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant(); }
                catch { return p.Trim().ToUpperInvariant(); }
            }

            // 自指链接（兼容链接的常见形态：指向自身/父级，访问被系统拒绝，表现为“内容为空”）
            if (string.Equals(Norm(linkPath), Norm(target)))
                return target + "  [自指链接]";
            if (!Directory.Exists(target))
                return target + "  [目标不存在]";
            // 经链接路径探测（即用户“点进去”的行为）：junction 自身被 ACL 拒绝时内容不可见
            try
            {
                using var e = Directory.EnumerateFileSystemEntries(linkPath).GetEnumerator();
                e.MoveNext();
            }
            catch (UnauthorizedAccessException)
            {
                return target + "  [无访问权限，内容不可见]";
            }
            catch { }
            return target;
        }

        /// <summary>复制全部扫描结果</summary>
        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_rows.Count == 0) return;
            var sb = new StringBuilder();
            sb.AppendLine("链接位置\t目标位置\t层数");
            foreach (var r in _rows)
                sb.AppendLine($"{r.LinkPath}\t{r.Target}\t{r.Depth}");
            try { Clipboard.SetText(sb.ToString()); }
            catch { /* 剪贴板占用等场景忽略 */ }
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null) return;
            _rows.Clear();
            StatusText.Visibility = Visibility.Collapsed;
        }

        private void SetBusy(bool busy)
        {
            ScanButton.IsEnabled = !busy;
            ScanButton.Content = busy ? "扫描中…" : "扫描";
            PathBox.IsEnabled = !busy;
            LimitBox.IsEnabled = !busy;
            DirLimitBox.IsEnabled = !busy;
            FollowLinksCheck.IsEnabled = !busy;
            ShowHiddenCheck.IsEnabled = !busy;
            CopyButton.IsEnabled = !busy;
            ClearButton.IsEnabled = !busy;
        }

        private void SetBusyDone()
        {
            _cts?.Dispose();
            _cts = null; // 允许下一次扫描
            ScanButton.IsEnabled = true;
            ScanButton.Content = "扫描";
            PathBox.IsEnabled = true;
            LimitBox.IsEnabled = true;
            DirLimitBox.IsEnabled = true;
            FollowLinksCheck.IsEnabled = true;
            ShowHiddenCheck.IsEnabled = true;
            CopyButton.IsEnabled = _rows.Count > 0;
            ClearButton.IsEnabled = true;
        }

        private void ShowStatus(string text, DialogKind kind)
        {
            StatusText.Text = text;
            StatusText.Foreground = kind switch
            {
                DialogKind.Warning => (Brush)FindResource("WarnBrush"),
                DialogKind.Error => (Brush)FindResource("DangerBrush"),
                _ => (Brush)FindResource("SuccessBrush")
            };
            StatusText.Visibility = Visibility.Visible;
        }

        /// <summary>扫描统计</summary>
        private sealed class ScanStat
        {
            public int Found;
            public int Dirs;
            public int Limited;
            public int DirLimited;
            public int MaxDepth;
        }
    }
}
