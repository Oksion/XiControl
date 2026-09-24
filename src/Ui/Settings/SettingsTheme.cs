namespace XiControl.Ui.Settings;

/// <summary>
/// Палитра окна настроек (Win11-стиль). Читается из системной темы на каждую пересборку
/// окна — тема могла смениться, пока окно было спрятано.
/// </summary>
public sealed class SettingsTheme
{
    public readonly bool Dark;
    public readonly Color WinBg, NavBg, Card, Border, Text, Text2, Accent, Sel, Field;

    private SettingsTheme(bool dark)
    {
        Dark = dark;
        if (dark)
        {
            WinBg = Color.FromArgb(32, 32, 32); NavBg = Color.FromArgb(38, 38, 38);
            Card = Color.FromArgb(45, 45, 45); Border = Color.FromArgb(56, 56, 56);
            Text = Color.FromArgb(240, 240, 240); Text2 = Color.FromArgb(200, 200, 200);
            Accent = Color.FromArgb(96, 205, 255); Sel = Color.FromArgb(52, 52, 52);
            Field = Color.FromArgb(39, 39, 39);
        }
        else
        {
            WinBg = Color.FromArgb(243, 243, 243); NavBg = Color.FromArgb(234, 234, 234);
            Card = Color.White; Border = Color.FromArgb(229, 229, 229);
            Text = Color.FromArgb(26, 26, 26); Text2 = Color.FromArgb(95, 95, 95);
            Accent = Color.FromArgb(0, 95, 184); Sel = Color.FromArgb(233, 233, 233);
            Field = Color.FromArgb(251, 251, 251);
        }
    }

    public static SettingsTheme Load() => new(Theme.IsDark());
}
