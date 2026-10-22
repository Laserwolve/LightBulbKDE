using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Threading;
using LightBulb.PlatformInterop;

namespace LightBulb;

public static class Program
{
    private static Assembly Assembly { get; } = Assembly.GetExecutingAssembly();

    public static string Name { get; } = Assembly.GetName().Name ?? "LightBulb";

    public static Version Version { get; } = Assembly.GetName().Version ?? new Version(0, 0, 0);

    public static string VersionString { get; } = Version.ToString(3);

    public static bool IsDevelopmentBuild { get; } = Version.Major is <= 0 or >= 999;

    public static string ExecutableDirPath { get; } = AppContext.BaseDirectory;

    // Outside Windows the app's launcher has no extension, and the process path is what names it
    public static string ExecutableFilePath { get; } =
        OperatingSystem.IsWindows()
            ? Path.ChangeExtension(Assembly.Location, "exe")
            : Environment.ProcessPath ?? Path.ChangeExtension(Assembly.Location, null);

    public static string ProjectUrl { get; } = "https://github.com/Laserwolve/LightBulbKDE";

    public static string ProjectReleasesUrl { get; } = $"{ProjectUrl}/releases";

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
            .With(
                new Win32PlatformOptions
                {
                    // Use redirection surface composition to avoid Avalonia's WinUI Composition
                    // renderer (WinUiCompositorConnection) from ticking endlessly via dcomp.dll
                    // when the application is idle, which would otherwise wake up dwm.exe
                    // continuously even when monitors are powered off.
                    CompositionMode = [Win32CompositionMode.RedirectionSurface],
                    RenderingMode = [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software],
                }
            )
            .LogToTrace();

    private static void OnTerminationRequested(PosixSignalContext context)
    {
        context.Cancel = true;
        Dispatcher.UIThread.Post(() => App.Shutdown());
    }

    [STAThread]
    public static int Main(string[] args)
    {
        // Ensure only one instance of the app is running at a time
        using var identityMutex = new Mutex(
            true,
            $"{Name}_Identity",
            out var isOnlyRunningInstance
        );

        if (!isOnlyRunningInstance)
            return 1;

        // Logging out ends apps with SIGTERM, which would otherwise exit without the cleanup
        // that leaves the screen untinted and undimmed
        using var terminationRegistration = !OperatingSystem.IsWindows()
            ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnTerminationRequested)
            : null;

        using var interruptionRegistration = !OperatingSystem.IsWindows()
            ? PosixSignalRegistration.Create(PosixSignal.SIGINT, OnTerminationRequested)
            : null;

        // Build and run the app
        var builder = BuildAvaloniaApp();

        try
        {
            return builder.StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            if (OperatingSystem.IsWindows())
                MessageBox.ShowError("Fatal Error", ex.ToString());
            else
                Console.Error.WriteLine(ex);

            throw;
        }
        finally
        {
            // Clean up after application shutdown
            if (builder.Instance is IDisposable disposableApp)
                disposableApp.Dispose();
        }
    }
}
