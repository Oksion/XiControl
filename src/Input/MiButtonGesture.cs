using XiControl.SystemIntegration;

namespace XiControl.Input;

/// <summary>
/// Стейт-машина жестов Mi-кнопки: одинарный клик / двойной клик / удержание.
/// Кормится сырыми Down/Up от прошивки (KeyMiDown/KeyMiUp), наружу отдаёт колбэки.
/// Чистая логика на IAppTimer — тестируется без железа и цикла сообщений.
/// Вызывать из одного (UI) потока — как и исходный код в TrayApp.
/// </summary>
public sealed class MiButtonGesture : IDisposable
{
    private readonly IAppTimer _hold;   // порог «долгого» нажатия
    private readonly IAppTimer _click;  // окно ожидания двойного клика
    private bool _handled;              // удержание уже сработало — Up не считаем кликом
    private int _clicks;

    /// <summary>Одинарный клик: после окна двойного, либо мгновенно, если двойной отключён.</summary>
    public Action? Click;

    /// <summary>Двойной клик (второй Up внутри окна).</summary>
    public Action? DoubleClick;

    /// <summary>Удержание дольше порога (Up после него кликом не считается).</summary>
    public Action? Hold;

    /// <summary>Включён ли жест двойного клика; false — одинарный срабатывает мгновенно,
    /// без окна ожидания ~300 мс (MiDoubleAction = "none").</summary>
    public Func<bool> DoubleEnabled = () => true;

    // holdMs/doubleClickMs настраиваются из config.json (AppConfig.MiHoldMs/MiDoubleClickMs);
    // клэмп снизу — чтобы кривое значение не сломало жест (мгновенное удержание / нулевое окно).
    public MiButtonGesture(IAppTimer? hold = null, IAppTimer? click = null,
        int holdMs = 400, int doubleClickMs = 300)
    {
        _hold = hold ?? new UiTimer();
        _hold.Interval = Math.Max(150, holdMs);
        _hold.Tick += OnHoldTimeout;

        _click = click ?? new UiTimer();
        _click.Interval = Math.Max(100, doubleClickMs);
        _click.Tick += OnClickTimeout;
    }

    /// <summary>Кнопка нажата (KeyMiDown).</summary>
    public void Down()
    {
        _handled = false;
        _hold.Stop();
        _hold.Start();
    }

    /// <summary>Кнопка отпущена (KeyMiUp).</summary>
    public void Up()
    {
        _hold.Stop();
        if (_handled) { _clicks = 0; return; }

        // двойной клик отключён — одинарный без задержки
        if (!DoubleEnabled())
        {
            Click?.Invoke();
            return;
        }

        _clicks++;
        _click.Stop();
        if (_clicks >= 2)
        {
            _clicks = 0;
            DoubleClick?.Invoke();
        }
        else
        {
            _click.Start(); // ждём: не начало ли это двойного
        }
    }

    private void OnClickTimeout()
    {
        _click.Stop();
        if (_clicks == 1)
            Click?.Invoke();
        _clicks = 0;
    }

    private void OnHoldTimeout()
    {
        _hold.Stop();
        if (_handled) return;
        _handled = true;
        _click.Stop();
        _clicks = 0;
        Hold?.Invoke();
    }

    public void Dispose()
    {
        _hold.Dispose();
        _click.Dispose();
    }
}
