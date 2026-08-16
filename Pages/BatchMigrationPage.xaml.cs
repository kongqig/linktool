using System;
using System.Collections.Generic;
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
    /// </summary>
    public partial class BatchMigrationPage : UserControl
    {
        private const string CommonToKey = "batchmigration.commonTo";

        /// <summary>迁移行列表（DataGrid 数据源）</summary>
        private readonly ObservableCollection<MigrateRow> _rows = new();

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
            public MigrateRow(string from, string? to)
            {
                _from = from;
                _to = to;
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
            var path = PathHistoryHelper.Browse("选择通用目标目录");
            if (path != null)
            {
                CommonToBox.Text = path;
                PathHistoryHelper.Remember(CommonToBox, CommonToKey);
            }
        }

        #endregion

        #region 添加 / 移除 / 清空

        /// <summary>粘贴添加：将多行文本中每行非空路径作为 From 加入列表</summary>
        private void AddPaste_Click(object sender, RoutedEventArgs e)
        {
            var text = PasteBox.Text ?? "";
            var lines = text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                           .Select(l => l.Trim())
                           .Where(l => l.Length > 0)
                           .ToList();
            foreach (var line in lines)
                _rows.Add(new MigrateRow(line, null));
            if (lines.Count > 0)
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
                if (Directory.Exists(path))
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

        /// <summary>处理单行：校验 → 锁检测 → 复制 → 删除源 → 创建链接</summary>
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

            // 3. 前置权限预检
            try
            {
                var denied = FileLockHelper.CheckFileAccessibility(from);
                if (denied.Count > 0)
                {
                    var shown = string.Join("; ", denied.Take(5));
                    if (denied.Count > 5) shown += $"; … 共 {denied.Count} 项";
                    MarkFailed(row, "无读取权限，请提权运行后重试：" + shown);
                    return;
                }
            }
            catch (Exception ex) { MarkFailed(row, "权限检查失败:" + ex.Message); return; }

            // 4. 前置锁定检测
            try
            {
                var locked = FileLockHelper.CheckFileLocks(from);
                if (locked.Count > 0)
                {
                    MarkFailed(row, "源文件夹被占用，请关闭占用程序后重试");
                    return;
                }
            }
            catch (Exception ex) { MarkFailed(row, "锁定检测失败:" + ex.Message); return; }

            // 4. 目标同名存在检查
            if (Directory.Exists(destDir))
            {
                bool empty;
                try { empty = !Directory.EnumerateFileSystemEntries(destDir).Any(); }
                catch { empty = false; }
                if (!empty) { MarkFailed(row, $"目标已存在且非空：{destDir}"); return; }
            }

            try
            {
                // 5. 复制
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

                // 6. 删除源（批量静默：不弹 UAC 提权）
                var deleted = FileOps.DeleteSourceDirectory(from, CancellationToken.None, allowAdminFallback: false);
                if (!deleted)
                {
                    MarkFailed(row, "源文件夹删除失败，请手动清理");
                    return;
                }

                // 7. 创建目录链接
                JunctionHelper.CreateJunction(from, destDir);

                Dispatcher.Invoke(row.MarkSuccess);
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
            StartButton.IsEnabled = !busy;
            StartButton.Content = busy ? "迁移中..." : "开始批量迁移";
            AddButton.IsEnabled = !busy;
            BrowseButton.IsEnabled = !busy;
            RemoveButton.IsEnabled = !busy;
            ClearButton.IsEnabled = !busy;
            CommonToBox.IsEnabled = !busy;
            PasteBox.IsEnabled = !busy;
        }

        #endregion
    }
}