using System.Threading;

namespace linktool.Services
{
    /// <summary>
    /// 全局任务占用标记：页面开始/结束长任务（迁移/链接/退迁及其批量版）时登记，
    /// 主窗口关闭时据此拦截，防止关闭程序导致复制不完整或数据异常。
    /// </summary>
    public static class BusyState
    {
        private static int _count;

        /// <summary>是否有任务正在执行</summary>
        public static bool IsBusy => Volatile.Read(ref _count) > 0;

        /// <summary>登记任务开始（与 End 必须配对调用，通常置于 try/finally）</summary>
        public static void Begin() => Interlocked.Increment(ref _count);

        /// <summary>登记任务结束</summary>
        public static void End() => Interlocked.Decrement(ref _count);
    }
}
