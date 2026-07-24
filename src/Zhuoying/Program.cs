using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;

namespace Zhuoying;

internal static class Program
{
    private static Mutex? _singleInstanceMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        // 单实例（REQUIREMENTS §3）；"唤起已有实例" 的通知机制留待后续阶段
        _singleInstanceMutex = new Mutex(true, @"Local\Zhuoying.SingleInstance", out var createdNew);
        if (!createdNew)
            return;

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
        finally
        {
            _singleInstanceMutex.ReleaseMutex();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
