using System.IO;
using System.Windows;
using System.Windows.Threading;
using DrawAim.Infrastructure.Logging;
using DrawAim.Infrastructure.Storage;

namespace DrawAim.App;

public partial class App : Application
{
    private RollingFileLogger? _startupLogger;

    protected override void OnStartup(StartupEventArgs e)
    {
        _startupLogger = CreateLoggerSafely();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        WriteFatalSafely("UI 线程发生未处理异常。", e.Exception);
        e.Handled = true;
        MessageBox.Show(
            "DrawAim 遇到无法继续的错误，诊断信息已写入本地日志。应用将安全退出。",
            "DrawAim",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        Shutdown(-1);
    }

    private void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            WriteFatalSafely("后台线程发生未处理异常。", exception);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteFatalSafely("后台任务异常未被观察。", e.Exception);
        e.SetObserved();
    }

    private void WriteFatalSafely(string message, Exception exception)
    {
        try
        {
            _startupLogger?.WriteAsync(DrawAimLogLevel.Critical, message, exception)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception loggingException) when (
            loggingException is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The original failure remains the actionable error.
        }
    }

    private static RollingFileLogger? CreateLoggerSafely()
    {
        try
        {
            return new RollingFileLogger(DrawAimDataPaths.Resolve());
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
