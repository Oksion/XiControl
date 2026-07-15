namespace XiControl.Localization;

public enum Lang { Ru = 0, En = 1, Zh = 2 }

/// <summary>Простая локализация RU/EN/ZH. Ключ → [ru, en, zh].</summary>
public static class Loc
{
    public static Lang Current = Lang.Ru;

    private static readonly Dictionary<string, string[]> S = new()
    {
        ["app.name"]        = ["Xi Control", "Xi Control", "Xi Control"],
        ["menu.charge"]     = ["Беречь батарею (80%)", "Battery care (80%)", "电池保护 (80%)"],
        ["menu.perf"]       = ["Режим производительности", "Performance mode", "性能模式"],
        ["mode.eco"]        = ["Эко", "Eco", "节能"],
        ["mode.quiet"]      = ["Тихий", "Quiet", "静音"],
        ["mode.turbo"]      = ["Турбо", "Turbo", "高性能"],
        ["mode.full"]       = ["Полная мощность", "Full speed", "全速"],
        ["mode.auto"]       = ["Авто", "Auto", "智能"],
        ["menu.settings"]   = ["Настройки", "Settings", "设置"],
        ["menu.show.eco"]   = ["Показывать «Эко»", "Show Eco mode", "显示节能模式"],
        ["menu.show.full"]  = ["Показывать «Полную мощность»", "Show Full speed mode", "显示全速模式"],
        ["menu.startmode"]         = ["Режим при старте", "Startup mode", "启动模式"],
        ["menu.startmode.restore"] = ["Восстанавливать последний", "Restore last used", "恢复上次使用"],
        ["menu.startmode.pin"]     = ["Закрепить текущий режим", "Pin current mode", "锁定当前模式"],
        ["menu.startmode.power"]   = ["Профили питания", "Power profiles", "电源配置"],
        ["menu.profile.ac"]        = ["При зарядке", "When charging", "接通电源时"],
        ["menu.profile.battery"]   = ["От батареи", "On battery", "使用电池时"],
        ["menu.profile.nochange"]  = ["Не менять", "Don’t change", "不更改"],
        ["menu.profile.brightness"]= ["Запоминать яркость", "Remember brightness", "记住亮度"],
        ["menu.mi.perf"]    = ["Клик Mi — производительность", "Mi click: performance", "Mi 单击切换性能"],
        ["menu.mi.double"]  = ["Двойной клик Mi", "Mi double click", "Mi 双击"],
        ["menu.key.charge"] = ["Клавиша настроек — заряд", "Settings key: charge", "设置键切换充电"],
        ["menu.owl"]        = ["Режим совы (не спать)", "Owl mode (keep awake)", "猫头鹰模式（保持唤醒）"],
        ["menu.hz"]         = ["Авто-герцовка ({0}/{1} Гц)", "Auto refresh rate ({0}/{1} Hz)", "自动刷新率（{0}/{1} Hz）"],
        ["menu.owl.enable"] = ["Включить режим совы", "Enable owl mode", "启用猫头鹰模式"],
        ["menu.language"]   = ["Язык", "Language", "语言"],
        ["menu.autostart"]  = ["Запускать с Windows", "Start with Windows", "开机自启动"],
        ["menu.exit"]       = ["Выход", "Exit", "退出"],

        ["panel.title"]     = ["Быстрые настройки", "Quick settings", "快速设置"],
        ["panel.charge"]    = ["Заряд батареи", "Battery charge", "电池充电"],
        ["panel.awake"]     = ["Не спать", "Keep awake", "保持唤醒"],

        ["menu.monitor"]    = ["Монитор", "Monitor", "监视器"],
        ["monitor.title"]   = ["Монитор", "Monitor", "监视器"],
        ["monitor.power"]   = ["Потребление", "Power draw", "功耗"],
        ["monitor.watts"]   = ["{0:0.0} Вт", "{0:0.0} W", "{0:0.0} 瓦"],
        ["monitor.na"]      = ["— от сети", "— on AC", "— 交流供电"],
        ["monitor.ram.of"]  = ["{0:0.0} / {1:0.0} ГБ", "{0:0.0} / {1:0.0} GB", "{0:0.0} / {1:0.0} GB"],
        ["monitor.watts.scale"] = ["{0:0} Вт", "{0:0} W", "{0:0} 瓦"],

        ["osd.charging"]         = ["Зарядка", "Charging", "充电中"],
        ["osd.charging.limited"] = ["Зарядка до {0}%", "Charging to {0}%", "充电至 {0}%"],
        ["osd.onbattery"]        = ["Работа от батареи", "On battery", "电池供电"],
        ["osd.level"]            = ["Заряд {0}%", "Battery {0}%", "电量 {0}%"],
        ["osd.care.on"]          = ["Беречь батарею", "Battery care", "电池保护"],
        ["osd.care.off"]         = ["Заряд до 100%", "Charge to 100%", "充电至 100%"],
        ["osd.mic.on"]           = ["Микрофон включён", "Microphone on", "麦克风已开启"],
        ["osd.mic.off"]          = ["Микрофон выключен", "Microphone off", "麦克风已静音"],
        ["osd.backlight"]        = ["Подсветка клавиатуры", "Keyboard backlight", "键盘背光"],
        ["osd.backlight.level"]  = ["{0}%", "{0}%", "{0}%"],
        ["osd.off"]              = ["Выключено", "Off", "已关闭"],
        ["osd.auto"]             = ["Авто", "Auto", "自动"],
        ["osd.hz"]               = ["{0} Гц", "{0} Hz", "{0} Hz"],
        ["osd.hz.on"]            = ["Авто-герцовка", "Auto refresh rate", "自动刷新率"],
        ["osd.hz.on.sub"]        = ["{0} Гц от сети, {1} Гц от батареи", "{0} Hz on AC, {1} Hz on battery", "交流电 {0} Hz，电池 {1} Hz"],
        ["osd.hz.off"]           = ["Авто-герцовка выключена", "Auto refresh rate off", "自动刷新率已关闭"],
        ["osd.fnlock"]           = ["Fn-Lock", "Fn-Lock", "Fn 锁定"],
        ["osd.fnlock.on"]        = ["Классические F1–F12", "Function keys F1–F12", "F1–F12 功能键"],
        ["osd.fnlock.off"]       = ["Мультимедийные клавиши", "Media keys", "多媒体按键"],

        ["err.title"]   = ["Ошибка", "Error", "错误"],
        ["err.noiface"] = [
            "Интерфейс MIFS не найден. Нужны права администратора и совместимый ноутбук Xiaomi/Redmi.",
            "MIFS interface not found. Requires administrator rights and a compatible Xiaomi/Redmi laptop.",
            "未找到 MIFS 接口。需要管理员权限和兼容的小米/红米（Redmi）笔记本电脑。"],
    };

    public static string T(string key)
        => S.TryGetValue(key, out var v) ? v[(int)Current] : key;

    public static string T(string key, params object[] args)
        => string.Format(T(key), args);
}
