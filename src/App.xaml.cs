using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using XiControl.Config;
using XiControl.Localization;
using XiControl.SystemIntegration;
using XiControl.Ui;
using XiControl.Wmi;

namespace XiControl;

public partial class App : Application, IDisposable
{
    private Mutex? _mutex;
    private ServiceProvider? _services;
    private StartupErrorWindow? _startupError;
    private bool _shuttingDown;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Log.Ex("WinUI.Unhandled", e.Exception);
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mutex = new Mutex(true, @"Global\XiControlMutex", out bool created);
        if (!created)
        {
            _mutex.Dispose();
            _mutex = null;
            Exit();
            return;
        }

        _services = ConfigureServices().BuildServiceProvider();
        var cfg = _services.GetRequiredService<AppConfig>();
        Log.Enabled = cfg.LogEnabled;
        FlyoutPalette.Apply(cfg.FlyoutTheme);
        if (AppPaths.FallbackReason is { } reason) Log.Write($"AppPaths: {reason}");
        else if (AppPaths.Portable) Log.Write($"AppPaths: portable-режим, данные в {AppPaths.DataDir}");
        _services.GetRequiredService<ILocalizer>().Current = cfg.Language ?? string.Empty;

        try
        {
            _ = _services.GetRequiredService<IMifsClient>();
        }
        catch (Exception ex)
        {
            ShowStartupError("Startup", ex);
            return;
        }

        var tray = _services.GetRequiredService<TrayApp>();
        tray.ExitRequested = Shutdown;
        try
        {
            tray.Start();
        }
        catch (Exception ex)
        {
            ShowStartupError("TrayApp.Start", ex);
        }
    }

    private void ShowStartupError(string stage, Exception exception)
    {
        Log.Ex(stage, exception);
        StartupDiagnostics.Write(stage, exception);
        try
        {
            _startupError = new StartupErrorWindow(
                Loc.T("err.title"), $"{Loc.T("err.noiface")}\n\n{exception.Message}")
            {
                Dismissed = Shutdown,
            };
            _startupError.Popup();
        }
        catch (Exception windowException)
        {
            StartupDiagnostics.Write("StartupErrorWindow", windowException);
            Shutdown();
        }
    }

    private static ServiceCollection ConfigureServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfigStore>(new JsonConfigStore());
        services.AddSingleton(sp => sp.GetRequiredService<IConfigStore>().Load());
        services.AddSingleton(ApiSettingsStore.Load());
        services.AddSingleton<ILocalizer, Localizer>();
        services.AddSingleton<IMifsClient, MifsClient>();
        services.AddSingleton<IKeyEventSource, MifsEventWatcher>();
        services.AddSingleton<SystemEventsSource>();
        services.AddSingleton<IPowerEvents>(sp => sp.GetRequiredService<SystemEventsSource>());
        services.AddSingleton<IDisplayEvents>(sp => sp.GetRequiredService<SystemEventsSource>());
        services.AddSingleton<TouchpadControl>();
        services.AddSingleton<TouchscreenControl>();
        services.AddSingleton<TouchpadDeadZone>();
        services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<AppConfig>();
            return new ChargeGuard(sp.GetRequiredService<IMifsClient>(), sp.GetRequiredService<IPowerEvents>(),
                () => cfg.ChargeCare && !cfg.TravelMode ? cfg.CarePercent() : 100);
        });
        services.AddSingleton<RefreshRateGuard>();
        services.AddSingleton<BrightnessCapGuard>();
        services.AddSingleton<AlsWatcher>();
        services.AddSingleton(sp => new AutoBrightnessGuard(
            sp.GetRequiredService<AppConfig>(), sp.GetRequiredService<IPowerEvents>(),
            clamp: (level, online) => sp.GetRequiredService<BrightnessCapGuard>().ClampRestore(level, online)));
        services.AddSingleton<PowerProfileGuard>();
        services.AddSingleton<TravelChargeMonitor>();
        services.AddSingleton<TrayIconController>();
        services.AddSingleton<AppController>();
        services.AddSingleton<TrayApp>();
        return services;
    }

    private void Shutdown()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        try { _services?.Dispose(); }
        catch (Exception ex) { Log.Ex("Shutdown", ex); }
        _services = null;
        _startupError?.Dispose();
        _startupError = null;
        _mutex?.Dispose();
        _mutex = null;
        Exit();
    }

    public void Dispose()
    {
        Shutdown();
        GC.SuppressFinalize(this);
    }
}
