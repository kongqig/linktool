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
    /// 批量链接页：通过后台任务逐行创建 Junction 目录链接（仅创建，不复制不删除）。
    /// </summary>
    public partial class BatchLinkPage : UserControl
    {
        private const string CommonLinkKey = "batchlink.commonLink"; // 通用链接位置历史键

        private readonly ObservableCollection<BatchLinkRow> _rows = new();
        private bool _running; // 是否正在批量处理

        public BatchLinkPage()
        {
            InitializeComponent();
            RowGrid.ItemsSource = _rows;
            PathHistoryHelper.Load(CommonLinkBox, CommonLinkKey);
        }

        // ---- 通用链接位置（默认目录）----
        private void CommonLinkBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var path = PathHistoryHelper.Browse("选择通用链接位置（默认目录）");
            if (path != null) { CommonLinkBox.Text = path; PathHistoryHelper.Remember(CommonLinkBox, CommonLinkKey); }
        }

        // ---- 粘贴添加（目标路径）----
        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var text = PasteBox.Text ?? "";
            foreach (var line in text.Split('\n'))
            {
                var p = line.Trim();
                if (p.Length > 0) _rows.Add(new BatchLinkRow("", p)); // 路径填入目标路径，链接位置留待填写
            }
            PasteBox.Clear();
        }

        /// <summary>选择：从文件夹对话框选取一个目标路径加入列表</summary>
        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var path = PathHistoryHelper.Browse("选择一个目标文件夹");
            if (path != null) _rows.Add(new BatchLinkRow("", path));
        }

        // ---- 拖拽添加 ----
        private void RowGrid_PreviewDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        /// <summary>从资源管理器拖入文件夹自动添加行（Preview 事件避免被 DataGrid 吞掉），路径填入目标路径</summary>
        private void RowGrid_PreviewDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
            foreach (var f in files)
            {
                var p = f.Trim();
                if (p.Length > 0 && Directory.Exists(p)) _rows.Add(new BatchLinkRow("", p));
            }
            e.Handled = true;
        }

        // ---- 移除选中 / 清空 ----
        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = RowGrid.SelectedItems.Cast<BatchLinkRow>().ToList();
            foreach (var r in selected) _rows.Remove(r);
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e) => _rows.Clear();

        // ---- 开始批量链接 ----
        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_running || _rows.Count == 0) return;

            if (!DialogHelper.Confirm("确认批量链接",
                $"将批量创建 {_rows.Count} 条目录链接（Junction）。\n\n是否继续？",
                "开始", danger: false))
                return;

            _running = true;
            SetRunningState(true);

            // 通用链接位置：未填写链接位置的行的链接默认创建到 该目录\目标文件夹名 下
            var commonLinkDir = CommonLinkBox.Text?.Trim() ?? "";
            if (commonLinkDir.Length > 0) PathHistoryHelper.Remember(CommonLinkBox, CommonLinkKey);

            // 重置为待处理；自动解析出的链接位置在下次运行前清空，避免基于上次结果产生嵌套路径
            foreach (var r in _rows)
            {
                if (r.IsAutoResolved) r.LinkPath = "";
                r.IsAutoResolved = false;
                r.Status = "待处理";
            }

            var total = _rows.Count;
            var done = 0;
            UpdateProgress(done, total);

            try
            {
                for (var i = 0; i < _rows.Count; i++)
                {
                    var row = _rows[i];
                    row.Status = "处理中";

                    // 该行目标路径（必填）
                    var target = row.Target?.Trim() ?? "";
                    if (string.IsNullOrWhiteSpace(target))
                    {
                        row.Status = "失败：目标路径为空";
                        done++;
                        UpdateProgress(done, total);
                        continue;
                    }

                    // 链接目录：未填行的链接=通用链接位置\目标名；已填行的链接=该目录\目标名（均与目标同名）
                    var rowLink = row.LinkPath?.Trim() ?? "";
                    var name = Path.GetFileName(target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    var baseDir = string.IsNullOrWhiteSpace(rowLink) ? commonLinkDir : rowLink;
                    if (string.IsNullOrWhiteSpace(baseDir))
                    {
                        row.Status = "失败：链接目录为空，且未设置通用链接位置";
                        done++;
                        UpdateProgress(done, total);
                        continue;
                    }
                    var linkPath = Path.Combine(baseDir, name);
                    row.LinkPath = linkPath; // 展示解析后的完整链接位置
                    row.IsAutoResolved = string.IsNullOrWhiteSpace(rowLink); // 记录本次是否为自动解析

                    // 后台执行逐行处理，返回 null 表示成功，否则为失败原因
                    var reason = await Task.Run(() => ProcessRow(linkPath, target));

                    row.Status = reason == null ? "成功" : "失败：" + reason;
                    done++;
                    UpdateProgress(done, total);
                }
            }
            finally
            {
                _running = false;
                SetRunningState(false);
            }
        }

        /// <summary>后台处理单行：校验并创建 Junction，返回失败原因；成功返回 null。</summary>
        private static string? ProcessRow(string linkPath, string target)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(linkPath)) return "链接位置为空";
                if (string.IsNullOrWhiteSpace(target)) return "目标路径为空";
                if (!Directory.Exists(target)) return $"目标路径不存在：{target}";
                if (JunctionHelper.IsReparsePoint(target)) return $"目标路径已是目录链接：{target}";
                JunctionHelper.CreateJunction(linkPath, target);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        // ---- 进度更新 ----
        private void UpdateProgress(int done, int total)
        {
            ProgressBar.Maximum = Math.Max(total, 1);
            ProgressBar.Value = done;
            ProgressCountText.Text = $"{done}/{total}";
        }

        // ---- 运行期间禁用编辑类按钮 ----
        private void SetRunningState(bool running)
        {
            StartButton.IsEnabled = !running;
            AddButton.IsEnabled = !running;
            BrowseButton.IsEnabled = !running;
            RemoveButton.IsEnabled = !running;
            ClearButton.IsEnabled = !running;
            CommonLinkBox.IsEnabled = !running;
            PasteBox.IsEnabled = !running;
            RowGrid.IsReadOnly = running;
        }
    }

    /// <summary>批量链接的编辑行模型（实现属性变更通知）。</summary>
    public class BatchLinkRow : INotifyPropertyChanged
    {
        private string _linkPath = "";
        private string? _target;
        private string _status = "待处理";

        /// <summary>当前 LinkPath 是否为上一次运行时自动解析生成（下次运行时需要重置，避免嵌套）</summary>
        public bool IsAutoResolved { get; set; }

        /// <summary>链接位置（创建 Junction 的路径）</summary>
        public string LinkPath
        {
            get => _linkPath;
            set { _linkPath = value; OnPropertyChanged(nameof(LinkPath)); }
        }

        /// <summary>目标路径；可留空表示使用通用目标目录</summary>
        public string? Target
        {
            get => _target;
            set { _target = value; OnPropertyChanged(nameof(Target)); }
        }

        /// <summary>状态：待处理 / 处理中 / 成功 / 失败:原因</summary>
        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(nameof(Status)); }
        }

        public BatchLinkRow() { }

        public BatchLinkRow(string linkPath, string? target = null)
        {
            _linkPath = linkPath;
            _target = target;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string propertyName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>按状态文本返回对应颜色画刷，用于状态列着色。</summary>
    public sealed class StatusBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = value as string ?? "";
            if (s.StartsWith("失败", StringComparison.Ordinal)) return GetBrush("DangerBrush");
            if (s == "处理中") return GetBrush("WarnBrush");
            if (s == "成功") return GetBrush("SuccessBrush");
            return GetBrush("TextSecondaryBrush");
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();

        private static Brush GetBrush(string key)
            => (Brush)Application.Current.FindResource(key);
    }
}