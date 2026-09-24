namespace XiControl.SystemIntegration;

/// <summary>
/// Сильное нажатие на тачпад (XIC-78) — чистая логика, без HID: палец продавил панель сильнее
/// порога. Ровно так же решает Xiaomi PC Manager (плагин <c>PluginGesture.dll</c>): Tip Pressure
/// контакта больше 500 — событие, один раз за касание, повтор только после отрыва всех пальцев.
///
/// Порог не произвольный. На TM2424 измерено: касание 35–55, обычный клик 125–150, сильное
/// нажатие 900–1200 — 500 стоит посередине с запасом в разы в обе стороны. То же число — третье
/// значение команды порога 0x5B (<c>thr, thr×0.33, 500, 400</c>); похоже, им же прошивка решает,
/// когда щёлкнуть второй раз (0x59), но это догадка, не проверено.
/// </summary>
public sealed class HeavyPressDetector(int threshold = HeavyPressDetector.DefaultThreshold)
{
    public const int DefaultThreshold = 500;

    private bool _fired;

    /// <summary>Очередной кадр касаний; true — сильное нажатие только что случилось.</summary>
    public bool Update(IReadOnlyList<TouchContact> contacts)
    {
        // отрыв всех пальцев — единственный взвод: удержание продавленного пальца, дрожь
        // давления у порога и ведение после нажатия повторов давать не должны
        if (contacts.Count == 0) { _fired = false; return false; }
        if (_fired) return false;
        foreach (var c in contacts)
            if (c.Pressure > threshold) { _fired = true; return true; }
        return false;
    }
}
