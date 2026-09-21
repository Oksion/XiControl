using System.Runtime.InteropServices;

namespace XiControl.Ui;

/// <summary>Где показывать OSD — сетка 3×3 по рабочей области экрана.</summary>
public enum OsdPosition
{
    TopLeft, Top, TopRight,
    Left, Center, Right,
    BottomLeft, Bottom, BottomRight,
}

/// <summary>Клавиши-фиксаторы, у которых уведомление можно выключить.
/// Scroll Lock сюда не входит намеренно: иконки для него есть, а события прошивка не шлёт —
/// показывать настройку для того, чего не бывает, значит врать.</summary>
public enum LockOsd { CapsLock, NumLock, FnLock, WinKeyLock }

/// <summary>
/// Расположение карточки OSD. Вынесено из <see cref="OsdForm"/> двумя частями: чистый расчёт
/// координат (<see cref="Locate"/>, под тестами) и выбор монитора (<see cref="TargetScreen"/>,
/// Win32 — тестами не проверить).
/// </summary>
public static class OsdPlacement
{
    /// <summary>Отступ от края рабочей области для восьми краевых позиций, px при 96 dpi.</summary>
    public const int Margin = 24;

    /// <summary>
    /// Доля высоты для среднего ряда. Не 0.5: OSD исторически висит чуть ниже середины —
    /// так карточка не накрывает то, на что человек смотрит. Значение сохранено, чтобы
    /// у всех, кто ничего не настраивал, плашка осталась ровно там же, где была.
    /// </summary>
    private const double MiddleBand = 0.60;

    /// <summary>
    /// Левый верхний угол карточки размера <paramref name="osd"/> в рабочей области
    /// <paramref name="wa"/>. <paramref name="margin"/> уже отмасштабирован под DPI экрана.
    /// Карточка шире или выше рабочей области прижимается к её началу, а не уезжает за край.
    /// </summary>
    public static Point Locate(Rectangle wa, Size osd, OsdPosition pos, int margin)
    {
        // скобки обязательны: switch-выражение связывает сильнее, чем % и /
        int col = (int)pos % 3, row = (int)pos / 3;

        int x = col switch
        {
            0 => wa.Left + margin,                      // левая колонка
            2 => wa.Right - osd.Width - margin,         // правая
            _ => wa.Left + (wa.Width - osd.Width) / 2,  // центр
        };

        int y = row switch
        {
            0 => wa.Top + margin,                        // верхний ряд
            2 => wa.Bottom - osd.Height - margin,        // нижний
            _ => wa.Top + (int)(wa.Height * MiddleBand), // средний — историческая линия
        };

        // средний ряд считает от доли высоты, а не от центра карточки: при высокой карточке
        // и низком экране она иначе свесилась бы за нижний край
        if (row == 1) y = Math.Min(y, wa.Bottom - osd.Height - margin);

        return new Point(Math.Max(wa.Left, x), Math.Max(wa.Top, y));
    }

    /// <summary>
    /// Монитор, на котором человек сейчас работает: экран активного окна, иначе экран под
    /// курсором, иначе основной.
    ///
    /// Раньше OSD жёстко считался от <c>Screen.PrimaryScreen</c> — на двух мониторах плашка
    /// про Caps Lock всплывала на «главном», даже если человек печатал на втором. Та же
    /// ошибка, что чинили в XIC-21 у авто-герцовки: «основной экран» и «экран, куда смотрят»
    /// — разные вещи.
    /// </summary>
    public static Screen TargetScreen()
    {
        try
        {
            nint fg = GetForegroundWindow();
            if (fg != 0) return Screen.FromHandle(fg);
            return Screen.FromPoint(Cursor.Position);
        }
        catch (Exception ex)
        {
            Log.Ex("OsdPlacement.TargetScreen", ex);
            return Screen.PrimaryScreen ?? Screen.AllScreens[0];
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}
