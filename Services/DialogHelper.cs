using linktool.Dialogs;

namespace linktool.Services
{
    /// <summary>
    /// 对话框辅助：统一使用现代化弹窗。
    /// </summary>
    public static class DialogHelper
    {
        /// <summary>弹出确认框，返回是否确认。danger=true 时确认按钮为红色。</summary>
        public static bool Confirm(string title, string message, string confirmText = "确定", bool danger = false)
        {
            var dlg = new ConfirmDialog(title, message, confirmText, danger);
            return dlg.ShowDialog() == true;
        }

        /// <summary>信息提示</summary>
        public static void Info(string title, string message) => Show(title, message, DialogKind.Info);

        /// <summary>警告提示</summary>
        public static void Warn(string title, string message) => Show(title, message, DialogKind.Warning);

        /// <summary>错误提示</summary>
        public static void Error(string title, string message) => Show(title, message, DialogKind.Error);

        /// <summary>按类型弹出消息</summary>
        public static void Show(string title, string message, DialogKind kind = DialogKind.Info)
        {
            var dlg = new MessageDialog(title, message, kind);
            dlg.ShowDialog();
        }
    }
}