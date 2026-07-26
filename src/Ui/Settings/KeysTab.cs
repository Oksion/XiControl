using XiControl.Config;
using XiControl.Localization;

namespace XiControl.Ui.Settings;

/// <summary>Вкладка «Клавиши»: действия Mi-кнопки и «мёртвых» клавиш (per-slot из конфига).</summary>
public sealed class KeysTab : SettingsPane
{
    // Общий список действий для всех клавиш; порядок = порядок в комбо
    private static readonly string[] KeyActionValues =
    [
        "modes", "charge", "panel", "owl", "monitor", "travel", "touchpad", "touchscreen",
        "projection", "settings", "copilot", "launch", "none",
    ];

    // Единственное действие с полем ввода команды — по нему решаем, пересобирать ли слот.
    private const string Launch = "launch";

    private readonly AppConfig _cfg;
    private readonly Action _rebuild;

    public KeysTab(SettingsToolkit ui, AppConfig cfg, Action rebuild) : base(ui)
    {
        _cfg = cfg;
        _rebuild = rebuild;

        ui.AddHeader(this, "settings.tab.keys", "settings.keys.sub");

        ui.AddGroup(this, "settings.keys.mi");
        AddKeySlot("settings.key.mi.click", "settings.key.mi.click.desc",
            () => cfg.MiClickAction, v => cfg.MiClickAction = v,
            () => cfg.MiClickCommand, v => cfg.MiClickCommand = v);
        AddKeySlot("settings.key.mi.double", "settings.key.mi.double.desc",
            () => cfg.MiDoubleAction, v => cfg.MiDoubleAction = v,
            () => cfg.MiDoubleCommand, v => cfg.MiDoubleCommand = v);
        ui.AddNote(this, "settings.keys.mi.hold");

        ui.AddGroup(this, "settings.keys.other");
        AddKeySlot("settings.key.settings", "settings.key.settings.desc",
            () => cfg.SettingsKeyAction, v => cfg.SettingsKeyAction = v,
            () => cfg.SettingsKeyCommand, v => cfg.SettingsKeyCommand = v);
        AddKeySlot("settings.key.ai", "settings.key.ai.desc",
            () => cfg.AiKeyAction, v => cfg.AiKeyAction = v,
            () => cfg.AiKeyCommand, v => cfg.AiKeyCommand = v);
        AddKeySlot("settings.key.proj", "settings.key.proj.desc",
            () => cfg.ProjKeyAction, v => cfg.ProjKeyAction = v,
            () => cfg.ProjKeyCommand, v => cfg.ProjKeyCommand = v);
    }

    // Слот клавиши: комбо со всеми действиями; для «Запустить программу…» ниже появляется
    // поле команды (путь + аргументы). Смена действия пересобирает окно (поле показать/убрать).
    private void AddKeySlot(string titleKey, string descKey,
        Func<string?> getAction, Action<string> setAction,
        Func<string?> getCommand, Action<string?> setCommand)
    {
        string cur = getAction() ?? "none";
        int idx = Array.IndexOf(KeyActionValues, cur);
        if (idx < 0) idx = Array.IndexOf(KeyActionValues, "none"); // неизвестное значение из конфига
        string prev = cur;
        Ui.AddRow(this, titleKey, descKey,
            Ui.Combo([.. KeyActionValues.Select(a => Loc.T("settings.act." + a))], idx, i =>
            {
                string val = KeyActionValues[i];
                setAction(val);
                _cfg.Save();
                // пересборка — только чтобы показать/убрать поле команды: на каждом шаге
                // выбора (колёсико, стрелки) она закрывала дропдаун и «сбрасывала» выбор
                bool rebuild = prev == Launch || val == Launch;
                prev = val;
                if (rebuild) _rebuild();
            }, Ui.Sc(210)));
        if (cur == Launch)
        {
            var tf = Ui.TextField(getCommand() ?? "", Ui.Sc(300), s =>
            { setCommand(string.IsNullOrWhiteSpace(s) ? null : s.Trim()); _cfg.Save(); });
            tf.PlaceholderText = "\"C:\\Program Files\\App\\app.exe\" --flag";
            Controls.Add(Ui.SubRow("settings.key.command", tf));
        }
    }
}
