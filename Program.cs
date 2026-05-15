using Avalonia;

namespace CoastalCommandCenter;

class Program
{
    private static bool _exceptionLoggingRegistered;

    [STAThread]
    public static void Main(string[] args)
    {
        RegisterGlobalExceptionLogging();

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    private static void RegisterGlobalExceptionLogging()
    {
        if (_exceptionLoggingRegistered)
        {
            return;
        }

        _exceptionLoggingRegistered = true;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Console.WriteLine($"[FATAL] Unhandled exception: {e.ExceptionObject}");
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Console.WriteLine($"[FATAL] Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
