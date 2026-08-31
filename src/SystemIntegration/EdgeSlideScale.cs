namespace XiControl.SystemIntegration;

/// <summary>
/// Пересчёт шагов жеста в проценты яркости и нажатия клавиш громкости (XIC-61) — чистая логика
/// под тестами.
///
/// <b>Зачем вообще отдельный класс.</b> Первая версия двигала яркость на 5% за шаг, а громкость
/// на одно нажатие <c>VK_VOLUME_UP</c>, то есть на 2% — и шкалы разъехались в два с половиной
/// раза: яркость проходилась за пару проходов вдоль края, громкость за четыре. Обе величины
/// теперь измеряются в ОДНИХ единицах (процент своей шкалы), а расхождение по громкости
/// добирается накоплением: два процента дают одно нажатие, остаток переносится.
///
/// Чувствительность задаётся тем, что человек и так чувствует пальцем: <b>сколько проходов
/// вдоль края нужно, чтобы пройти шкалу целиком</b>. Один — резко, три — плавно.
/// </summary>
public sealed class EdgeSlideScale(int swipesPerRange)
{
    /// <summary>Доля высоты панели на один шаг жеста. Мелкая нарочно: шкала должна ощущаться
    /// непрерывной, а крупность движения задаётся чувствительностью, а не размером шага.</summary>
    public const double StepFraction = 0.02;

    /// <summary>Шагов в одном полном проходе вдоль края (сверху вниз).</summary>
    public const int StepsPerSwipe = (int)(1 / StepFraction);

    /// <summary>Шаг громкости Windows по <c>VK_VOLUME_UP</c> — 2% шкалы. Отсюда и пересчёт
    /// процентов в нажатия: дробить мельче нечем.</summary>
    private const double VolumeTapPercent = 2.0;

    /// <summary>Пресеты для настроек: сколько проходов на всю шкалу.</summary>
    public static readonly int[] Presets = [1, 2, 3];

    /// <summary>Клэмп: config.json правится руками, а ноль проходов — деление на ноль.
    /// Верх совпадает с набором пресетов намеренно: иначе вписанное руками значение не имело
    /// бы подписи, и комбо в настройках показывало бы не то, что реально работает.</summary>
    public static int Normalize(int swipes) => Math.Clamp(swipes, Presets[0], Presets[^1]);

    private readonly double _perStep = 100.0 / Normalize(swipesPerRange) / StepsPerSwipe;
    private double _brightness;
    private double _volume;

    /// <summary>Процент своей шкалы на один шаг жеста — для диагностики и тестов.</summary>
    public double PercentPerStep => _perStep;

    /// <summary>Целые проценты яркости из шагов; дробный остаток переносится в следующий вызов,
    /// иначе медленное движение не давало бы ничего вовсе.</summary>
    public int Brightness(int steps) => Take(ref _brightness, steps, 1.0);

    /// <summary>Число нажатий клавиши громкости: 2% шкалы за нажатие, остаток переносится.</summary>
    public int VolumeTaps(int steps) => Take(ref _volume, steps, VolumeTapPercent);

    /// <summary>Забыть накопленное — палец оторван, новый жест считает с нуля.</summary>
    public void Reset()
    {
        _brightness = 0;
        _volume = 0;
    }

    private int Take(ref double acc, int steps, double unit)
    {
        acc += steps * _perStep;
        int whole = (int)(acc / unit);
        acc -= whole * unit;
        return whole;
    }
}
