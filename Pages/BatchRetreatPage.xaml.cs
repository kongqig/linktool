using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using linktool.Services;

namespace linktool.Pages
{
    /// <summary>
    /// 批量退迁页：逐行退迁迁移生成的目录链接。静默后台处理。
    /// </summary>
    public partial class BatchRetreatPage : UserControl
    {
        private readonly ObservableCollection<BatchRetreatRow> _rows = new();
        private bool _running; // 是否正在批量处理
        private bool _calcRunning; // 计算全部运行中

        public BatchRetreatPage()
        {
            InitializeComponent();
            RowGrid.ItemsSource = _rows;
        }

        // ---- 添加 ----
        /// <summary>粘贴添加：换行分行，每行取第1列作为链接路径；引号段自动提取；无效路径标红</summary>
        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var lines = PastePathParser.Parse(PasteBox.Text ?? "");
            if (lines.Count == 0) return;

            var added = 0;
            foreach (var line in lines)
            {
                if (_rows.Any(r => string.Equals(r.LinkPath?.Trim(), line.Col1, StringComparison.OrdinalIgnoreCase)))
                    continue; // 去重

                var row = new BatchRetreatRow(line.Col1);
                if (!Directory.Exists(line.Col1))
                {
                    row.IsInvalid = true;
                    row.Status = "路径不存在";
                }
                _rows.Add(row);
                added++;
            }
            if (added > 0)
                PasteBox.Clear();
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var path = PathHistoryHelper.Browse("选择迁移后的链接路径");
            if (path != null) _rows.Add(new BatchRetreatRow(path));
        }

        // ---- 大小计算 ----

        /// <summary>计算单行链接目标内容大小（行内“算”按钮）</summary>
        private async void CalcRowSize_Click(object sender, RoutedEventArgs e)
        {
            if (_running || _calcRunning) return;
            if (sender is not FrameworkElement { DataContext: BatchRetreatRow row }) return;
            await CalcRowSizeAsync(row);
        }

        /// <summary>计算全部：按列表顺序逐行解析链接目标并计算内容大小（串行）</summary>
        private async void CalcAll_Click(object sender, RoutedEventArgs e)
        {
            if (_running || _calcRunning) return;
            var targets = _rows.Where(r => !string.IsNullOrWhiteSpace(r.LinkPath)).ToList();
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

        /// <summary>解析链接目标并计算内容大小（退迁时将复制回链接路径的内容）</summary>
        private async Task CalcRowSizeAsync(BatchRetreatRow row)
        {
            var link = row.LinkPath?.Trim() ?? "";
            try
            {
                if (link.Length == 0 || !Directory.Exists(link) || !JunctionHelper.IsReparsePoint(link))
                {
                    row.SizeText = "无法计算";
                    return;
                }
                var target = RetreatHelper.ResolveTarget(link);
                row.SizeText = "计算中…";
                var result = await SizeHelper.GetSizeAsync(target, CancellationToken.None);
                row.SizeText = $"{SizeHelper.Format(result.TotalBytes)} · {result.FileCount} 个文件"
                               + (result.Incomplete ? "（部分无法读取）" : "");
            }
            catch
            {
                row.SizeText = "无法计算";
            }
        }

        // ---- 拖拽添加 ----
        private void RowGrid_PreviewDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void RowGrid_PreviewDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
            foreach (var f in files)
            {
                var p = f.Trim();
                if (p.Length > 0 && Directory.Exists(p)) _rows.Add(new BatchRetreatRow(p));
            }
            e.Handled = true;
        }

        // ---- 移除 / 清空 ----
        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = RowGrid.SelectedItems.Cast<BatchRetreatRow>().ToList();
            foreach (var r in selected) _rows.Remove(r);
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e) => _rows.Clear();

        // ---- 开始批量退迁 ----
        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (StartButton.IsEnabled == false || _rows.Count == 0) return;

            if (!DialogHelper.Confirm("确认批量退迁",
                $"将批量退迁 {_rows.Count} 条目录链接（解析目标 → 复制回原路径 → 删除链接与旧目标）。\n\n是否继续？",
                "开始", danger: true))
                return;

            SetRunningState(true);
            foreach (var r in _rows) r.Reset();
            _running = true;
            BusyState.Begin();

            var total = _rows.Count;
            var done = 0;
            UpdateProgress(0, total);

            try
            {
                for (var i = 0; i < _rows.Count; i++)
                {
                    var row = _rows[i];
                    row.Status = "处理中";
                    var reason = await Task.Run(() => ProcessRow(row));
                    row.Status = reason == null ? "成功" : "失败：" + reason;
                    done++;
                    UpdateProgress(done, total);
                }
            }
            finally
            {
                _running = false;
                SetRunningState(false);
                BusyState.End();
            }
        }

        /// <summary>后台处理单行：解析目标 → 扫描（权限/占用/大小）→ 空间检查 → 退迁，成功返回 null，否则返回失败原因。</summary>
        private static string? ProcessRow(BatchRetreatRow row)
        {
            try
            {
                var link = row.LinkPath?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(link)) return "链接路径为空";
                if (!Directory.Exists(link)) return $"链接路径不存在：{link}";
                if (!JunctionHelper.IsReparsePoint(link)) return "该路径不是目录链接，无需退迁";

                var target = RetreatHelper.ResolveTarget(link);
                if (!Directory.Exists(target)) return $"链接目标不存在：{target}";
                if (JunctionHelper.IsReparsePoint(target)) return "链接目标仍是目录链接，请先处理目标后再退迁";

                // 前置综合扫描：权限 + 占用 + 大小（一次遍历）
                var scan = FileLockHelper.ScanDirectory(target);
                if (scan.Denied.Count > 0)
                    return $"无读取权限，请提权运行后重试（{scan.Denied.Count} 个文件无法访问）";
                if (scan.Locked.Count > 0)
                {
                    var procs = FileLockHelper.GetLockingProcesses(target);
                    if (procs.Count == 0) procs = FileLockHelper.GetProcessesLockingDirectory(target);
                    var names = procs.Count > 0
                        ? string.Join(", ", procs.Select(p => $"{p.ProcessName}(PID {p.Id})"))
                        : "未知进程";
                    return "目标被占用：" + names;
                }

                // 空间检查（含 1GB 余量；复制回链接路径所在盘，批量静默直接失败）
                var free = SizeHelper.GetFreeSpace(link);
                if (free != null)
                {
                    var required = scan.TotalBytes + SizeHelper.SpaceMarginBytes;
                    if (free.Value < required)
                        return $"磁盘空间不足：需要 {SizeHelper.Format(required)}，可用 {SizeHelper.Format(free.Value)}";
                }

                row.Target = target;
                RetreatHelper.Retreat(link);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private void UpdateProgress(int done, int total)
        {
            ProgressBar.Maximum = Math.Max(total, 1);
            ProgressBar.Value = done;
            ProgressCountText.Text = $"{done}/{total}";
        }

        private void SetRunningState(bool running)
        {
            StartButton.IsEnabled = !running;
            AddButton.IsEnabled = !running;
            BrowseButton.IsEnabled = !running;
            RemoveButton.IsEnabled = !running;
            ClearButton.IsEnabled = !running;
            PasteBox.IsEnabled = !running;
            RowGrid.IsReadOnly = running;
        }
    }

    /// <summary>批量退迁的行模型</summary>
    public class BatchRetreatRow : INotifyPropertyChanged
    {
        private string _linkPath = "";
        private string? _target;
        private string _status = "待处理";
        private string _sizeText = "—";
        private bool _isInvalid;

        public string LinkPath { get => _linkPath; set { _linkPath = value; OnPropertyChanged(nameof(LinkPath)); } }
        public string? Target { get => _target; set { _target = value; OnPropertyChanged(nameof(Target)); } }
        public string Status { get => _status; set { _status = value; OnPropertyChanged(nameof(Status)); } }

        /// <summary>链接目标内容大小显示（手动点击“算”后填充）</summary>
        public string SizeText { get => _sizeText; set { _sizeText = value; OnPropertyChanged(nameof(SizeText)); } }

        /// <summary>链接路径不存在（标红）</summary>
        public bool IsInvalid { get => _isInvalid; set { _isInvalid = value; OnPropertyChanged(nameof(IsInvalid)); } }

        public BatchRetreatRow() { }
        public BatchRetreatRow(string linkPath) => _linkPath = linkPath;

        public void Reset()
        {
            _target = null;
            _status = "待处理";
            OnPropertyChanged(nameof(Target));
            OnPropertyChanged(nameof(Status));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string propertyName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}