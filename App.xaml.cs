using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using GameColorGuard.Core;

namespace GameColorGuard;

public partial class App : Application
{
    // Önceki sürümle aynı ad: eski ve yeni sürüm aynı anda ekran renklerine müdahale edemez.
    private const string MutexName = "Local\\CS2ColorGuard_SingleInstance";
    private Mutex? _mutex;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string[] args = e.Args;

        if (args.Length >= 2 && args[0].Equals("--render", StringComparison.OrdinalIgnoreCase))
        {
            int code = RenderTest.Run(args[1], args.Length >= 3 ? args[2] : "idle");
            Shutdown(code);
            return;
        }
        if (args.Length >= 2 && args[0].Equals("--beforeafter", StringComparison.OrdinalIgnoreCase))
        {
            Shutdown(BeforeAfter.Run(args));
            return;
        }
        if (args.Length >= 4 && args[0].Equals("--fpsbanner", StringComparison.OrdinalIgnoreCase))
        {
            Shutdown(BeforeAfter.Banner(args[1], args[2], args[3]));
            return;
        }
        if (args.Length >= 2 && args[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
        {
            Shutdown(SelfTest.Run(args[1]));
            return;
        }

        _mutex = new Mutex(true, MutexName, out bool createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            MessageBox.Show("Game Color Guard zaten çalışıyor. Sistem tepsisini kontrol edin.",
                "Game Color Guard", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            if (ex.ExceptionObject is Exception exception) AppLogger.Write("İşlenmeyen hata", exception);
        };

        bool minimizedArg = args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        var window = new MainWindow(testMode: false, initialSettings: null, forceHidden: minimizedArg);
        MainWindow = window;
        if (!window.StartsHidden) window.Show();
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.Write("Beklenmeyen hata", e.Exception);
        MessageBox.Show($"Beklenmeyen hata:\n\n{e.Exception.Message}", "Game Color Guard",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_mutex is not null)
        {
            try
            {
                if (_ownsMutex) _mutex.ReleaseMutex();
            }
            catch
            {
                // Çıkışta yoksayılabilir.
            }
            _mutex.Dispose();
        }
        base.OnExit(e);
    }
}
