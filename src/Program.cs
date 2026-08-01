using Microsoft.Extensions.DependencyInjection;
using XiControl.Config;
using XiControl.Localization;
using XiControl.SystemIntegration;
using XiControl.Ui;
using XiControl.Wmi;

namespace XiControl;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // единственный экземпляр
        using var mutex = new Mutex(true, @"Global\XiControlMutex", out bool created);
        if (!created) return;

        ApplicationConfiguration.Initialize();

        // Граф объектов: все singleton, провайдер владеет Dispose (в обратном порядке создания).
        var services = new ServiceCollection();
        services.AddSingleton<IConfigStore>(new JsonConfigStore());
        services.AddSingleton(sp => sp.GetRequiredService<IConfigStore>().Load());
        // настройки HTTP API — отдельный файл с ACL (ProgramData), НЕ config.json: включить API
        // или подменить токен правкой пользовательского конфига невозможно (XIC-13)
        services.AddSingleton(ApiSettingsStore.Load());
        services.AddSingleton<ILocalizer, Localizer>();
        services.AddSingleton<IMifsClient, MifsClient>();
        services.AddSingleton<IKeyEventSource, MifsEventWatcher>();
        // один источник системных событий под двумя узкими швами (питание + экран):
        // окно-маршалер внутри нужно ровно одно
        services.AddSingleton<SystemEventsSource>();
        services.AddSingleton<IPowerEvents>(sp => sp.GetRequiredService<SystemEventsSource>());
        services.AddSingleton<IDisplayEvents>(sp => sp.GetRequiredService<SystemEventsSource>());
        services.AddSingleton<TouchpadControl>();
        services.AddSingleton<TouchscreenControl>();
        services.AddSingleton<TouchpadDeadZone>();
        // «В дорогу» временно снимает защиту (заряд до 100%) — гард бережёт 80% только когда travel выключен
        services.AddSingleton(sp =>
        {
            var c = sp.GetRequiredService<AppConfig>();
            return new ChargeGuard(sp.GetRequiredService<IMifsClient>(), sp.GetRequiredService<IPowerEvents>(),
                () => c.ChargeCare && !c.TravelMode ? c.CarePercent() : 100);
        });
        services.AddSingleton<RefreshRateGuard>();
        services.AddSingleton<PowerProfileGuard>();
        services.AddSingleton<TravelChargeMonitor>();
        services.AddSingleton<TrayIconController>();
        services.AddSingleton<AppController>();
        services.AddSingleton<TrayApp>();
        using var provider = services.BuildServiceProvider();

        var cfg = provider.GetRequiredService<AppConfig>();
        Log.Enabled = cfg.LogEnabled; // до этой строчки лог включён — ошибки старта не теряем
        provider.GetRequiredService<ILocalizer>().Current = cfg.Language ?? ""; // Loc нормализует пустую/неизвестную культуру
        Ui.FlyoutPalette.Apply(cfg.FlyoutTheme); // тема панелей/OSD — до создания форм

        try
        {
            _ = provider.GetRequiredService<IMifsClient>(); // ранняя проверка железа (ctor бросает без MIFS)
        }
        catch (Exception ex)
        {
            Log.Ex("Startup", ex);
            MessageBox.Show(
                Loc.T("err.noiface") + "\n\n" + ex.Message,
                Loc.T("err.title"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        provider.GetRequiredService<TrayApp>().Start();
        Application.Run();
    }
}
