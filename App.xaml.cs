using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using linktool.Services;

namespace linktool
{
    /// <summary>
    /// App.xaml 的交互逻辑
    /// 应用程序入口类
    /// </summary>
    public partial class App : Application
    {
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("kernel32.dll")]
        private static extern uint GetConsoleProcessList(uint[] pids, uint count);

        private const int SW_HIDE = 0;

        /// <summary>
        /// 构造：挂接全局未处理异常，避免静默崩溃并便于定位问题。
        /// </summary>
        public App()
        {
            DispatcherUnhandledException += (_, e) =>
            {
                DialogHelper.Error("错误", $"发生未处理的异常：\n{e.Exception}");
                e.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                DialogHelper.Error("错误", $"发生严重错误：\n{e.ExceptionObject}");
            };
        }

        /// <summary>
        /// 启动分流：有命令行参数则执行 CLI 并退出；否则启动 GUI（并隐藏双击产生的控制台窗口）。
        /// </summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            if (e.Args.Length > 0)
            {
                var code = Cli.CliRunner.Run(e.Args);
                Environment.Exit(code);
                return;
            }

            HideConsoleIfStandalone();
            new MainWindow().Show();
        }

        /// <summary>
        /// 程序为控制台子系统：双击启动（无父控制台）会出现一个控制台窗口，这里隐藏它。
        /// 若从终端启动（有父 shell 共用控制台），则不隐藏，保持终端可用。
        /// 判断：GetConsoleProcessList 返回 1 表示这是本进程独占的新控制台（双击场景）。
        /// </summary>
        private static void HideConsoleIfStandalone()
        {
            var hwnd = GetConsoleWindow();
            if (hwnd == IntPtr.Zero) return;
            var pids = new uint[2];
            var n = GetConsoleProcessList(pids, 2);
            if (n <= 1)
                ShowWindow(hwnd, SW_HIDE);
        }
    }
}