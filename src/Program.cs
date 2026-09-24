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
        WatchForCrashes();

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
        services.AddSingleton<TouchpadEdgeSliders>();
        services.AddSingleton<TouchpadHaptics>();
        // «В дорогу» временно снимает защиту (заряд до 100%) — гард бережёт 80% только когда travel выключен
        services.AddSingleton(sp =>
        {
            var c = sp.GetRequiredService<AppConfig>();
            return new ChargeGuard(sp.GetRequiredService<IMifsClient>(), sp.GetRequiredService<IPowerEvents>(),
                () => c.ChargeCare && !c.TravelMode ? c.CarePercent() : 100);
        });
        services.AddSingleton<RefreshRateGuard>();
        services.AddSingleton<BrightnessCapGuard>(); // лимит яркости (XIC-29); события ему раздаёт PowerProfileGuard
        services.AddSingleton<AlsWatcher>();         // датчик освещённости (XIC-30); стартует в AppController.Startup
        // авто-яркость: кламп выхода — лимитом (кривая хранит намерение, лимит — фильтр)
        services.AddSingleton(sp => new AutoBrightnessGuard(
            sp.GetRequiredService<AppConfig>(), sp.GetRequiredService<IPowerEvents>(),
            clamp: (level, online) => sp.GetRequiredService<BrightnessCapGuard>().ClampRestore(level, online)));
        services.AddSingleton<PowerProfileGuard>();
        services.AddSingleton<TravelChargeMonitor>();
        services.AddSingleton<ChargeLimitWatcher>();   // «заряд дошёл до порога» → вебхук (XIC-75)
        services.AddSingleton<TrayIconController>();
        services.AddSingleton<AppController>();
        services.AddSingleton<TrayApp>();
        using var provider = services.BuildServiceProvider();

        var cfg = provider.GetRequiredService<AppConfig>();
        Log.Enabled = cfg.LogEnabled; // до этой строчки лог включён — ошибки старта не теряем
        // портативный режим сам себя не логирует (AppPaths нельзя звать Log во время
        // инициализации — рекурсия), поэтому докладываем здесь, когда каталог уже выбран
        if (AppPaths.FallbackReason is { } why) Log.Write($"AppPaths: {why}");
        else if (AppPaths.Portable) Log.Write($"AppPaths: портативный режим, данные в {AppPaths.DataDir}");
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

    /// <summary>
    /// Последняя сеть перед смертью процесса. Сама по себе она ничего не чинит — исключение,
    /// дошедшее сюда, приложение уже не переживёт, — но превращает «зависла и закрылась» в
    /// строку с местом падения. До XIC-63 такие падения не оставляли вообще ничего: тестер
    /// сообщал о вылете, а в журнале была тишина, и разбирать было нечего.
    ///
    /// Сами исключения ловятся там, где возникают (таймеры пула, поток чтения касаний,
    /// WMI-вызовы); сюда доходит только то, что мы не предусмотрели.
    /// </summary>
    private static void WatchForCrashes()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Log.Ex("Необработанное исключение", ex);
            else Log.Write($"Необработанное исключение: {e.ExceptionObject}");
        };

        // исключение из Task, которое никто не дождался: процесс не падает, но проблему
        // видеть надо — иначе ошибки фоновых операций пропадают бесследно
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Ex("Необработанное исключение в задаче", e.Exception);
            e.SetObserved();
        };

        // UI-поток: без этого WinForms показал бы поверх экрана диалог с трассировкой стека.
        // Пишем в журнал и живём дальше — трей-утилита не должна пугать человека простынёй.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Ex("Исключение в UI-потоке", e.Exception);
    }
}
