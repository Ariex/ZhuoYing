using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;

namespace Zhuoying;

internal static class Program
{
    private static Mutex? _singleInstanceMutex;

    [STAThread]
    public static int Main(string[] args)
    {
        // Agent API（--api-* / --mcp）：无头即用即退，不触碰单实例互斥/托盘/热键，
        // 与运行中的托盘实例互不干扰
        if (Agent.AgentCli.TryRun(args, out var exitCode))
            return exitCode;

        // 单实例（REQUIREMENTS §3）；"唤起已有实例" 的通知机制留待后续阶段
        _singleInstanceMutex = new Mutex(true, @"Local\Zhuoying.SingleInstance", out var createdNew);
        if (!createdNew)
            return 0;

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
        finally
        {
            _singleInstanceMutex.ReleaseMutex();
        }
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
