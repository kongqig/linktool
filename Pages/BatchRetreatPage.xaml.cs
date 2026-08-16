using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
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

        public BatchRetreatPage()
        {
            InitializeComponent();
            RowGrid.ItemsSource = _rows;
        }

        // ---- 添加 ----
        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var text = PasteBox.Text ?? "";
            foreach (var line in text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = line.Trim();
                if (p.Length > 0) _rows.Add(new BatchRetreatRow(p));
            }
            PasteBox.Clear();
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var path = PathHistoryHelper.Browse("选择迁移后的链接路径");
            if (path != null) _rows.Add(new BatchRetreatRow(path));
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
                SetRunningState(false);
            }
        }

        /// <summary>后台处理单行：解析目标并退迁，成功返回 null，否则返回失败原因。</summary>
        private static string? ProcessRow(BatchRetreatRow row)
        {
            try
            {
                var link = row.LinkPath?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(link)) return "链接路径为空";
                if (!Directory.Exists(link)) return $"链接路径不存在：{link}";
                if (!JunctionHelper.IsReparsePoint(link)) return "该路径不是目录链接，无需退迁";

                var target = RetreatHelper.ResolveTarget(link);
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

        public string LinkPath { get => _linkPath; set { _linkPath = value; OnPropertyChanged(nameof(LinkPath)); } }
        public string? Target { get => _target; set { _target = value; OnPropertyChanged(nameof(Target)); } }
        public string Status { get => _status; set { _status = value; OnPropertyChanged(nameof(Status)); } }

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