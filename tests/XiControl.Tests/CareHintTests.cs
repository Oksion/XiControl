using FluentAssertions;
using XiControl.Localization;
using XiControl.Ui.Settings;
using XiControl.Wmi;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// Пояснение к порогу «беречь батарею» (XIC-25): группировка уровней по сценарию.
/// Сама вкладка — WinForms, её глазами; здесь только выбор ключа и наличие переводов.
/// </summary>
public sealed class CareHintTests
{
    [Theory]
    [InlineData(40, "settings.battery.care.hint.low")]
    [InlineData(50, "settings.battery.care.hint.low")]
    [InlineData(60, "settings.battery.care.hint.mid")]
    [InlineData(70, "settings.battery.care.hint.mid")]
    [InlineData(80, "settings.battery.care.hint.high")]
    public void CareHintKey_GroupsLevelsByScenario(int percent, string expected)
    {
        BatteryTab.CareHintKey(percent).Should().Be(expected);
    }

    [Theory]
    [InlineData(0)]      // мусор из config.json
    [InlineData(45)]     // не из нашего набора
    [InlineData(100)]    // защита выключена
    [InlineData(255)]
    public void CareHintKey_NeverThrows_OnValuesOutsidePresets(int percent)
    {
        BatteryTab.CareHintKey(percent).Should().StartWith("settings.battery.care.hint.");
    }

    [Fact]
    public void EveryFirmwarePreset_HasHintText()
    {
        // набор уровней зависит от модели — добавится новый, а текста к нему не будет
        foreach (int p in Mifs.ChargeCarePresets)
        {
            string key = BatteryTab.CareHintKey(p);
            Loc.T(key).Should().NotBe(key, $"уровню {p}% нужен переведённый текст, а не голый ключ");
        }
    }
}
