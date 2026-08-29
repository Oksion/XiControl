using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using XiControl.Config;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// Конвертер KeyCodes (XIC-38). Смысл у него один: человек правит config.json руками, и
/// System.Text.Json по умолчанию бросает исключение на числе без кавычек — а его ловит
/// JsonConfigStore.Load и откатывает ВЕСЬ конфиг к дефолтам. То есть одна забытая кавычка
/// в кодах клавиш стирала бы все настройки. Поэтому тесты идут через AppConfig, а не через
/// голый конвертер: проверяем и разбор, и то, ради чего он написан — что остальной конфиг
/// переживает кривую правку.
/// </summary>
public sealed class LenientStringMapConverterTests
{
    private static AppConfig Parse(string keyCodesJson) =>
        JsonSerializer.Deserialize<AppConfig>(
            $$"""{ "Language": "zh", "ChargeCare": true, "KeyCodes": {{keyCodesJson}} }""")!;

    [Fact]
    public void Строки_читаются_как_есть()
    {
        var cfg = Parse("""{ "miDown": "0x18", "miUp": "0x19" }""");

        cfg.KeyCodes.Should().NotBeNull();
        cfg.KeyCodes!["miDown"].Should().Be("0x18");
        cfg.KeyCodes["miUp"].Should().Be("0x19");
    }

    // Слоты в KeyMap ищутся без учёта регистра — словарь должен приехать с тем же компаратором,
    // иначе "MIDOWN" в конфиге молча перестанет находиться.
    [Fact]
    public void Ключи_нечувствительны_к_регистру()
    {
        var cfg = Parse("""{ "MIDOWN": "0x18" }""");

        cfg.KeyCodes!.ContainsKey("midown").Should().BeTrue();
        cfg.KeyCodes["miDown"].Should().Be("0x18");
    }

    // Ровно тот случай, ради которого конвертер и написан: без него здесь исключение.
    [Theory]
    [InlineData("24", "24")]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    public void Число_и_булево_без_кавычек_читаются_строкой(string raw, string expected)
    {
        var cfg = Parse($$"""{ "miDown": {{raw}} }""");

        cfg.KeyCodes!["miDown"].Should().Be(expected);
    }

    // Число форматируется InvariantCulture. На ru-RU разделитель — запятая, и без явной
    // культуры дробное значение приехало бы как «24,5»; в этом репозитории на культуре
    // уже спотыкались (PR #40 привёз два теста, падавших на любой не-английской локали).
    [Fact]
    public void Дробное_число_не_зависит_от_культуры()
    {
        var prev = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
        try
        {
            Parse("""{ "miDown": 24.5 }""").KeyCodes!["miDown"].Should().Be("24.5");
        }
        finally { CultureInfo.CurrentCulture = prev; }
    }

    // null = «слот не задан»: записи быть не должно, иначе KeyMap попробует разобрать пустоту.
    [Fact]
    public void Null_у_слота_не_создаёт_записи()
    {
        var cfg = Parse("""{ "miDown": "0x18", "miUp": null }""");

        cfg.KeyCodes!.Should().ContainKey("miDown");
        cfg.KeyCodes.Should().NotContainKey("miUp");
    }

    // Объект и массив здесь бессмысленны — пропускаем молча, но соседние слоты обязаны выжить:
    // кривая правка не должна отбирать у человека рабочие клавиши.
    [Theory]
    [InlineData("""{ "miDown": "0x18", "bad": { "nested": 1 }, "miUp": "0x19" }""")]
    [InlineData("""{ "miDown": "0x18", "bad": [1, 2, 3], "miUp": "0x19" }""")]
    public void Объект_и_массив_пропускаются_а_соседи_выживают(string json)
    {
        var cfg = Parse(json);

        var codes = cfg.KeyCodes!;
        codes.Should().NotContainKey("bad");
        codes["miDown"].Should().Be("0x18");
        codes["miUp"].Should().Be("0x19");
    }

    [Fact]
    public void Пустой_объект_даёт_пустую_карту()
    {
        Parse("{ }").KeyCodes.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Null_вместо_карты_даёт_null()
    {
        Parse("null").KeyCodes.Should().BeNull();
    }

    // Главное свойство: чем бы ни оказался KeyCodes, разбор конфига НЕ падает и остальные
    // настройки доезжают. Иначе JsonConfigStore.Load откатит человеку всё к дефолтам.
    [Theory]
    [InlineData("null")]
    [InlineData("5")]
    [InlineData(""" "не карта" """)]
    [InlineData("[1, 2]")]
    [InlineData("""{ "miDown": { "oops": true } }""")]
    public void Любой_мусор_в_KeyCodes_не_роняет_остальной_конфиг(string keyCodesJson)
    {
        var cfg = Parse(keyCodesJson);

        cfg.Language.Should().Be("zh", "соседние настройки не должны пострадать");
        cfg.ChargeCare.Should().BeTrue();
    }

    [Fact]
    public void Запись_и_чтение_дают_ту_же_карту()
    {
        var cfg = new AppConfig { KeyCodes = new() { ["miDown"] = "0x18", ["miUp"] = "0x19" } };

        var back = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(cfg))!;

        back.KeyCodes.Should().BeEquivalentTo(cfg.KeyCodes);
    }
}
