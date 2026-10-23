using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using linktool.Services;

namespace linktool.Pages
{
    /// <summary>
    /// 批量迁移页：将一个或多个文件夹批量迁移到目标位置（复制 → 删除源 → 创建目录链接）。
    /// 支持多对一（共用通用目标目录）与多对多（每行单独指定 To），后台顺序执行、静默自动。
    /// 粘贴支持 源|目标 两列；无效路径标红；行大小手动计算；执行前逐行权限/占用/空间检查。
    /// </summary>
    public partial class BatchMigrationPage : UserControl
    {
        private const string CommonToKey = "batchmigration.commonTo";

        /// <summary>迁移行列表（DataGrid 数据源）</summary>
        private readonly ObservableCollection<MigrateRow> _rows = new();

        private bool _busy; // 批量迁移运行中
        private bool _calcRunning; // 计算全部运行中

        public BatchMigrationPage()
        {
            InitializeComponent();
            PathHistoryHelper.Load(CommonToBox, CommonToKey);
            Grid.ItemsSource = _rows;
        }

        #region 行模型

        /// <summary>单行迁移记录</summary>
        public sealed class MigrateRow : INotifyPropertyChanged
        {
            private string _from = "";
            private string? _to;
            private string _status = "待处理";
            private string _sizeText = "—";
            private bool _isInvalidFrom;
            private bool _isInvalidTo;
            private bool _isRunning;
            private int _state; // 0=待处理/处理中, 1=成功, 2=失败

            /// <summary>源文件夹（可编辑）</summary>
            public string From
            {
                get => _from;
                set { _from = value; OnPropertyChanged(); }
            }

            /// <summary>目标文件夹（可空，空则用通用目标目录）</summary>
            public string? To
            {
                get => _to;
                set { _to = value; OnPropertyChanged(); }
            }

            /// <summary>状态显示文本</summary>
            public string Status
            {
                get => _status;
                set { _status = value; OnPropertyChanged(); }
            }

            /// <summary>源文件夹大小显示（手动点击“算”后填充）</summary>
            public string SizeText
            {
                get => _sizeText;
                set { _sizeText = value; OnPropertyChanged(); }
            }

            /// <summary>源路径不存在（标红）</summary>
            public bool IsInvalidFrom
            {
                get => _isInvalidFrom;
                set { _isInvalidFrom = value; OnPropertyChanged(); }
            }

            /// <summary>行目标路径不存在（标红）</summary>
            public bool IsInvalidTo
            {
                get => _isInvalidTo;
                set { _isInvalidTo = value; OnPropertyChanged(); }
            }

            /// <summary>是否正在处理（着色：处理中/警告）</summary>
            public bool IsRunning
            {
                get => _isRunning;
                set { _isRunning = value; OnPropertyChanged(); }
            }

            /// <summary>是否成功（着色：成功）</summary>
            public bool IsSuccess => _state == 1;

            /// <summary>是否失败（着色：失败）</summary>
            public bool IsFailed => _state == 2;

            /// <summary>构造</summary>
            public MigrateRow(string from, string? to, bool invalidFrom = false, bool invalidTo = false)
            {
                _from = from;
                _to = to;
                _isInvalidFrom = invalidFrom;
                _isInvalidTo = invalidTo;
                if (invalidFrom || invalidTo) _status = "路径不存在";
            }

            /// <summary>标记为成功</summary>
            public void MarkSuccess()
            {
                _state = 1;
                IsRunning = false;
                Status = "成功";
                OnPropertyChanged(nameof(IsSuccess));
                OnPropertyChanged(nameof(IsFailed));
            }

            /// <summary>标记为失败</summary>
            public void MarkFailed(string reason)
            {
                _state = 2;
                IsRunning = false;
                Status = "失败:" + reason;
                OnPropertyChanged(nameof(IsSuccess));
                OnPropertyChanged(nameof(IsFailed));
            }

            /// <summary>标记为处理中</summary>
            public void MarkRunning()
            {
                _state = 0;
                IsRunning = true;
                Status = "处理中";
                OnPropertyChanged(nameof(IsSuccess));
                OnPropertyChanged(nameof(IsFailed));
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            private void OnPropertyChanged([CallerMemberName] string? name = null)
                => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        #endregion

        #region 路径输入

        /// <summary>回车记录通用目标目录历史</summary>
        private void CommonTo_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                PathHistoryHelper.Remember(CommonToBox, CommonToKey);
        }

        private void CommonToBrowse_Click(object sender, RoutedEventArgs e)
        {
            var path = PathHistoryHelper.Browse("选择通用目标目录", CommonToBox, CommonToKey);
            if (path != null)
            {
                CommonToBox.Text = path;
                PathHistoryHelper.Remember(CommonToBox, CommonToKey);
            }
        }

        /// <summary>算可用空间：显示通用目标目录所在磁盘的可用空间</summary>
        private void CommonSpace_Click(object sender, RoutedEventArgs e)
        {
            var path = CommonToBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(path))
            {
                DialogHelper.Warn("提示", "请先填写通用目标目录。");
                return;
            }
            var free = SizeHelper.GetFreeSpace(path);
            CommonSpaceText.Text = free == null ? "无法获取可用空间" : $"可用 {SizeHelper.Format(free.Value)}";
        }

        #endregion

        #region 添加 / 移除 / 清空

        /// <summary>粘贴添加：解析多行文本（换行分行、“|”分列、引号段提取）加入列表</summary>
        private void AddPaste_Click(object sender, RoutedEventArgs e)
        {
            var lines = PastePathParser.Parse(PasteBox.Text ?? "");
            if (lines.Count == 0) return;

            var added = 0;
            foreach (var line in lines)
            {
                // 去重：相同主路径不重复添加
                if (_rows.Any(r => string.Equals(r.From?.Trim(), line.Col1, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var invalidFrom = !Directory.Exists(line.Col1);
                var invalidTo = line.Col2 != null && !Directory.Exists(line.Col2);
                _rows.Add(new MigrateRow(line.Col1, line.Col2, invalidFrom, invalidTo));
                added++;
            }
            if (added > 0)
                PasteBox.Text = "";
        }

        /// <summary>移除选中的行</summary>
        private void RemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = Grid.SelectedItems.Cast<MigrateRow>().ToList();
            foreach (var row in selected)
                _rows.Remove(row);
        }

        /// <summary>选择：从文件夹对话框选取一个源文件夹加入列表</summary>
        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var path = PathHistoryHelper.Browse("选择一个要迁移的文件夹");
            if (path != null)
                _rows.Add(new MigrateRow(path, null));
        }

        /// <summary>清空所有行</summary>
        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            _rows.Clear();
            ResetProgress();
        }

        #endregion

        #region 大小计算

        /// <summary>计算单行源文件夹大小（行内“算”按钮）</summary>
        private async void CalcRowSize_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _calcRunning) return;
            if (sender is not FrameworkElement { DataContext: MigrateRow row }) return;
            await CalcRowSizeAsync(row);
        }

        /// <summary>计算全部：按列表顺序逐行计算（串行，避免磁盘过载）</summary>
        private async void CalcAll_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _calcRunning) return;
            var targets = _rows.Where(r => !string.IsNullOrWhiteSpace(r.From)).ToList();
            if (targets.Count == 0) return;

            _calcRunning = true;
            CalcAllButton.IsEnabled = false;
            try
            {
                foreach (var row in targets)
                    await CalcRowSizeAsync(row);
            }
            finally
            {
                _calcRunning = false;
                CalcAllButton.IsEnabled = true;
            }
        }

        private async Task CalcRowSizeAsync(MigrateRow row)
        {
            var from = row.From?.Trim() ?? "";
            if (from.Length == 0 || !Directory.Exists(from))
            {
                row.SizeText = "无法计算";
                return;
            }
            row.SizeText = "计算中…";
            try
            {
                var result = await SizeHelper.GetSizeAsync(from, CancellationToken.None);
                row.SizeText = $"{SizeHelper.Format(result.TotalBytes)} · {result.FileCount} 个文件"
                               + (result.Incomplete ? "（部分无法读取）" : "");
            }
            catch
            {
                row.SizeText = "计算失败";
            }
        }

        #endregion

        #region 拖拽添加

        private void Grid_PreviewDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        /// <summary>从资源管理器拖入文件夹自动添加行（用 Preview 事件确保不被 DataGrid 吞掉）</summary>
        private void Grid_PreviewDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
                return;
            foreach (var path in files)
            {
                if (Directory.Exists(path) &&
                    !_rows.Any(r => string.Equals(r.From?.Trim(), path.Trim(), StringComparison.OrdinalIgnoreCase)))
                    _rows.Add(new MigrateRow(path, null));
            }
            e.Handled = true;
        }

        #endregion

        #region 批量迁移

        /// <summary>开始批量迁移：后台顺序处理每一行</summary>
        private async void Start_Click(object sender, RoutedEventArgs e)
        {
            if (_rows.Count == 0)
            {
                DialogHelper.Warn("提示", "请先添加要迁移的文件夹。");
                return;
            }

            // 待处理行数
            var pendingCount = _rows.Count(r => !r.IsSuccess && !r.IsFailed);
            if (pendingCount == 0)
            {
                DialogHelper.Warn("提示", "没有可迁移的行。");
                return;
            }

            if (!DialogHelper.Confirm("确认批量迁移",
                $"将批量迁移 {pendingCount} 个文件夹（复制 → 删除源 → 创建目录链接）。\n\n是否继续？",
                "开始", danger: true))
                return;

            var commonTo = CommonToBox.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(commonTo))
                PathHistoryHelper.Remember(CommonToBox, CommonToKey);

            // 收集待处理行，过滤掉已成功/已失败的行
            var pending = _rows.Where(r => !r.IsSuccess && !r.IsFailed).ToList();
            if (pending.Count == 0)
            {
                DialogHelper.Warn("提示", "没有可迁移的行。");
                return;
            }

            SetBusy(true);
            ResetProgress();
            ProgressBar.Maximum = pending.Count;
            BusyState.Begin();

            try
            {
                await Task.Run(() => RunBatch(pending, commonTo));
            }
            catch (Exception ex)
            {
                DialogHelper.Error("错误", $"批量迁移过程中出错：{ex.Message}");
            }
            finally
            {
                BusyState.End();
                SetBusy(false);
            }
        }

        /// <summary>后台逐行处理</summary>
        private void RunBatch(List<MigrateRow> pending, string commonTo)
        {
            var completed = 0;
            foreach (var row in pending)
            {
                // 更新整体进度：当前行开始
                Dispatcher.Invoke(() =>
                {
                    row.MarkRunning();
                    UpdateProgress(completed, pending.Count, $"正在处理：{row.From}");
                });

                // 复制回调：更新当前文件与整体进度（文本带文件级 [已完成/总数]，避免进度信息被忽略）
                ProcessRow(row, commonTo, (c, t, f) =>
                    Dispatcher.Invoke(() => UpdateProgress(completed, pending.Count, $"正在复制 [{c}/{t}] {f}")));

                completed++;
                Dispatcher.Invoke(() => UpdateProgress(completed, pending.Count, ""));
            }
        }

        /// <summary>处理单行：校验 → 扫描（权限/占用/大小）→ 空间检查 → 复制 → 删除源 → 创建链接</summary>
        private void ProcessRow(MigrateRow row, string commonTo, ProgressHandler copyProgress)
        {
            var from = row.From?.Trim() ?? "";
            var to = row.To?.Trim();
            var effectiveTo = string.IsNullOrEmpty(to) ? commonTo : to;

            // 1. 校验：通用目标与行 To 至少有一个
            if (string.IsNullOrWhiteSpace(effectiveTo))
            { MarkFailed(row, "目标目录为空，请填写通用目标目录或该行 To"); return; }
            var error = MoveValidator.ValidateRow(from, effectiveTo);
            if (error != null) { MarkFailed(row, error); return; }

            // 2. 目标目录计算
            var fromName = Path.GetFileName(from.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var destDir = Path.Combine(effectiveTo!, fromName);

            // 3. 前置综合扫描：权限 + 占用 + 大小（一次遍历）
            ScanResult scan;
            try { scan = FileLockHelper.ScanDirectory(from); }
            catch (Exception ex) { MarkFailed(row, "扫描失败:" + ex.Message); return; }

            if (scan.Denied.Count > 0)
            {
                var shown = string.Join("; ", scan.Denied.Take(5));
                if (scan.Denied.Count > 5) shown += $"; … 共 {scan.Denied.Count} 项";
                MarkFailed(row, "无读取权限，请提权运行后重试：" + shown);
                return;
            }

            if (scan.Locked.Count > 0)
            {
                var procs = FileLockHelper.GetLockingProcesses(from);
                if (procs.Count == 0) procs = FileLockHelper.GetProcessesLockingDirectory(from);
                var names = procs.Count > 0
                    ? string.Join(", ", procs.Select(p => $"{p.ProcessName}(PID {p.Id})"))
                    : "未知进程";
                MarkFailed(row, "源被占用：" + names);
                return;
            }

            // 4. 空间检查（含 1GB 余量；批量静默直接失败，不弹窗）
            var free = SizeHelper.GetFreeSpace(effectiveTo!);
            if (free != null)
            {
                var required = scan.TotalBytes + SizeHelper.SpaceMarginBytes;
                if (free.Value < required)
                {
                    MarkFailed(row, $"磁盘空间不足：需要 {SizeHelper.Format(required)}，可用 {SizeHelper.Format(free.Value)}");
                    return;
                }
            }

            // 5. 目标位置已是目录链接 → 写入会穿透到链接目标内部，直接判失败
            if (Directory.Exists(destDir) && JunctionHelper.IsReparsePoint(destDir))
            {
                MarkFailed(row, $"目标位置已是目录链接，写入会穿透到链接目标内部：{destDir}");
                return;
            }

            // 6. 目标同名存在检查
            if (Directory.Exists(destDir))
            {
                bool empty;
                try { empty = !Directory.EnumerateFileSystemEntries(destDir).Any(); }
                catch { empty = false; }
                if (!empty) { MarkFailed(row, $"目标已存在且非空：{destDir}"); return; }
            }

            try
            {
                // 7. 复制
                var allFiles = FileOps.GetAllFiles(from);
                if (allFiles.Count == 0)
                {
                    Directory.CreateDirectory(destDir);
                }
                else
                {
                    using var locker = new DirectoryLockManager();
                    locker.Lock(destDir);
                    FileOps.CopyDirectoryFull(from, destDir, allFiles, CancellationToken.None, copyProgress);
                }

                // 8. 删除源（批量静默：不弹 UAC 提权）
                var deleted = FileOps.DeleteSourceDirectory(from, CancellationToken.None, allowAdminFallback: false);
                if (!deleted)
                {
                    MarkFailed(row, "源文件夹删除失败，请手动清理");
                    return;
                }

                // 9. 创建目录链接
                JunctionHelper.CreateJunction(from, destDir);

                Dispatcher.Invoke(() =>
                {
                    row.MarkSuccess();
                    if (scan.ReparseDirs.Count > 0)
                        row.Status = $"成功（跳过 {scan.ReparseDirs.Count} 个链接子目录）";
                });
            }
            catch (JunctionHelperJunctionException ex)
            {
                MarkFailed(row, "目录链接创建失败:" + ex.Message);
            }
            catch (Exception ex)
            {
                MarkFailed(row, ex.Message);
            }
        }

        /// <summary>在 UI 线程标记行失败</summary>
        private void MarkFailed(MigrateRow row, string reason)
            => Dispatcher.Invoke(() => row.MarkFailed(reason));

        private void UpdateProgress(int completed, int total, string current)
        {
            ProgressCountText.Text = $"{completed}/{total}";
            ProgressBar.Maximum = Math.Max(1, total);
            ProgressBar.Value = completed;
            CurrentFileText.Text = current;
        }

        private void ResetProgress()
        {
            ProgressCountText.Text = "0/0";
            ProgressBar.Value = 0;
            CurrentFileText.Text = "";
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            StartButton.IsEnabled = !busy;
            StartButton.Content = busy ? "迁移中..." : "开始批量迁移";
            AddButton.IsEnabled = !busy;
            BrowseButton.IsEnabled = !busy;
            RemoveButton.IsEnabled = !busy;
            ClearButton.IsEnabled = !busy;
            CalcAllButton.IsEnabled = !busy && !_calcRunning;
            CommonSpaceButton.IsEnabled = !busy;
            CommonToBox.IsEnabled = !busy;
            PasteBox.IsEnabled = !busy;
        }

        #endregion
    }
}
