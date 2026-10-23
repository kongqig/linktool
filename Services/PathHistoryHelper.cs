using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace linktool.Services
{
    /// <summary>
    /// 路径输入与历史下拉辅助：加载/记忆路径历史，弹出文件夹选择。
    /// </summary>
    public static class PathHistoryHelper
    {
        /// <summary>将 ComboBox 的 ItemsSource 刷新为指定键的历史路径</summary>
        public static void Load(ComboBox combo, string key)
            => combo.ItemsSource = AppSettings.Instance.GetHistory(key);

        /// <summary>记录当前文本到历史并刷新下拉（保留原文本）</summary>
        public static void Remember(ComboBox combo, string key)
        {
            var text = combo.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;
            AppSettings.Instance.Remember(key, text);
            Load(combo, key);
            combo.Text = text;
        }

        /// <summary>
        /// 弹出文件夹选择对话框，返回路径或 null。
        /// 初始目录优先级：编辑框当前文本（目录存在）→ 该键历史最新一条（目录存在）→ 系统默认。
        /// </summary>
        public static string? Browse(string description, ComboBox? combo = null, string? historyKey = null)
        {
            var dialog = new OpenFolderDialog { Title = description };

            var initial = combo?.Text?.Trim();
            if (string.IsNullOrEmpty(initial) && historyKey != null)
            {
                var history = AppSettings.Instance.GetHistory(historyKey);
                initial = history.Count > 0 ? history[0] : null;
            }
            if (!string.IsNullOrEmpty(initial) && System.IO.Directory.Exists(initial))
                dialog.InitialDirectory = initial;

            return dialog.ShowDialog() == true ? dialog.FolderName : null;
        }
    }
}