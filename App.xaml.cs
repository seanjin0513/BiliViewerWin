using System;
using System.Windows;
using System.Windows.Threading;

namespace BiliViewer
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 兜底：任何未处理异常弹窗提示，方便定位（同时写日志到 %LOCALAPPDATA%\BiliViewer\error.log）
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
                Log("AppDomain: " + args.ExceptionObject);
        }

        void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log("Dispatcher: " + e.Exception);
            MessageBox.Show(
                "程序发生错误：\n\n" + e.Exception.Message +
                "\n\n详细信息已写入 %LOCALAPPDATA%\\BiliViewer\\error.log",
                "BiliViewer", MessageBoxButton.OK, MessageBoxImage.Error);
            // 不崩溃，保持窗口可用
            e.Handled = true;
        }

        internal static void Log(string text)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Settings.AppDataDir);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(Settings.AppDataDir, "error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + text + Environment.NewLine);
            }
            catch
            {
                // 忽略日志失败
            }
        }
    }
}
