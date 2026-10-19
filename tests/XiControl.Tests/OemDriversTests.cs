using System.Text;
using System.Text.Json;
using FluentAssertions;
using XiControl.SystemIntegration;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// Проверка драйверов у OEM (XIC-95): протокол и сверка — чистая логика, сеть не трогаем.
/// Эталон подписи сверен на захвате живого запроса службы (docs/16); сюда он не попал —
/// в том теле хеш серийника машины.
/// </summary>
public sealed class OemDriversTests
{
    // ---- протокол ----

    [Fact]
    public void Тело_с_ключами_по_алфавиту_и_без_sn()
    {
        string body = OemProtocol.BuildBody("driver", OemProtocol.DriverModules("NPU"), "TM2424", "ru_RU", "RU", "26200");

        body.Should().Be("{\"category\":\"driver\",\"lan\":\"ru_RU\",\"modules\":[{\"moduleName\":\"NPU\"}]," +
                         "\"product\":\"TM2424\",\"region\":\"RU\",\"winBuildNumber\":\"26200\"}");
    }

    [Fact]
    public void Подпись_md5_в_верхнем_регистре()
    {
        string sign = OemProtocol.Sign("{}");

        sign.Should().MatchRegex("^[0-9A-F]{32}$");
        OemProtocol.Sign("{}").Should().Be(sign, "подпись детерминирована");
        OemProtocol.Sign("{ }").Should().NotBe(sign, "любой лишний символ ломает подпись — поэтому тело компактное");
    }

    [Fact]
    public void Post_несёт_тело_и_заголовок_в_base64_в_кавычках()
    {
        string body = OemProtocol.BuildBody("bios", OemProtocol.BiosModules("1.0.0.2570"), "TM2424", "en_US", "US", "26200");
        string post = OemProtocol.BuildPost(body);

        post.Should().StartWith("data=\"").And.EndWith("\"");
        string root = Encoding.UTF8.GetString(Convert.FromBase64String(post[6..^1]));
        root.Should().Be($"{{\"body\":{body},\"header\":{{\"appid\":\"{OemProtocol.AppId}\",\"sign\":\"{OemProtocol.Sign(body)}\"}}}}");
        using var doc = JsonDocument.Parse(root); // и это валидный JSON
        doc.RootElement.GetProperty("body").GetProperty("modules")[0].GetProperty("version").GetString().Should().Be("1.0.0.2570");
    }

    [Theory]
    [InlineData("XMAPT4B0P0A0A", "1.0.0.2570")] // TM2424: 0x0A0A = 2570
    [InlineData("ABC0001", "1.0.0.0001")]
    [InlineData("XMAPT4B0P0AZZ", null)]         // хвост не hex — правило не подошло
    [InlineData("A1", null)]
    [InlineData(null, null)]
    public void Версия_BIOS_из_последних_четырёх_hex(string? smbios, string? expected) =>
        OemProtocol.BiosRequestVersion(smbios).Should().Be(expected);

    [Fact]
    public void Ответ_разбирается_с_версиями_из_hardwareIds()
    {
        const string json = """
            {"code":0,"message":"ok","modules":[{"moduleName":"NPU","latestVer":{"version":"NS",
              "packages":[{"type":"FULL_PACKAGE","md5":"9e6f","fileSize":35632547,"fileName":"npu.zip",
                           "mirrors":["https://cdn.example/npu.zip"]}],
              "strategy":{"hardwareIds":[{"id":"PCI\\VEN_8086&DEV_B03E","version":"32.0.100.4841"},
                                         {"id":"PCI\\VEN_8086&DEV_FD3E","version":"32.0.100.4841"}]}}}]}
            """;

        var modules = OemProtocol.ParseResponse(json);

        modules.Should().ContainSingle();
        var m = modules![0];
        m.Name.Should().Be("NPU");
        m.Package.Should().Be(new OemPackage("npu.zip", 35632547, "9e6f", "https://cdn.example/npu.zip"));
        m.HardwareIds.Should().HaveCount(2).And.AllSatisfy(h => h.Version.Should().Be("32.0.100.4841"));
    }

    private const string TwoProducts = """
        {"code":0,"message":"ok","modules":[
          {"product":"TM2424","moduleName":"FWSC","latestVer":{"packages":[{"fileName":"our.zip","fileSize":1,"md5":"a",
            "mirrors":["https://cdn/our.zip"]}],"strategy":{"hardwareIds":[{"id":"USB\\VID_6789&PID_1234&REV_1236","version":"740B08"}]}}},
          {"product":"TM2309","moduleName":"FWSC","latestVer":{"packages":[{"fileName":"alien.zip","fileSize":1,"md5":"b",
            "mirrors":["https://cdn/alien.zip"]}],"strategy":{"hardwareIds":[{"id":"USB\\VID_6789&PID_1234&REV_1111","version":"999999"}]}}},
          {"moduleName":"FWTP","latestVer":{"packages":[{"fileName":"noproduct.zip","fileSize":1,"md5":"c",
            "mirrors":["https://cdn/noproduct.zip"]}],"strategy":{"hardwareIds":[]}}}]}
        """;

    [Fact]
    public void Модуль_чужой_модели_отбрасывается() =>
        OemProtocol.ParseResponse(TwoProducts, "TM2424")!.Select(m => m.Package!.FileName)
            .Should().Equal("our.zip", "noproduct.zip"); // без поля product не отбрасываем: доказать «чужую модель» нечем

    [Fact]
    public void Модель_сравнивается_без_учёта_регистра() =>
        OemProtocol.ParseResponse(TwoProducts, "tm2424")!.Select(m => m.Package!.FileName)
            .Should().Contain("our.zip").And.NotContain("alien.zip");

    [Fact]
    public void Без_модели_фильтра_нет() =>
        OemProtocol.ParseResponse(TwoProducts).Should().HaveCount(3);

    [Fact]
    public void Пустые_modules_это_нет_пакета_а_не_ошибка() =>
        OemProtocol.ParseResponse("{\"code\":0,\"message\":\"ok\",\"modules\":[]}").Should().BeEmpty();

    [Theory]
    [InlineData("{\"code\":500,\"message\":\"签名校验失败\"}")] // неверная подпись
    [InlineData("<html>502</html>")]
    [InlineData("{\"message\":\"ok\"}")]
    public void Ошибка_сервера_или_мусор_это_null(string json) =>
        OemProtocol.ParseResponse(json).Should().BeNull();

    [Fact]
    public void Ссылка_только_https()
    {
        const string json = """
            {"code":0,"modules":[{"moduleName":"X","latestVer":{"packages":[{"fileName":"x.zip","fileSize":1,"md5":"a",
              "mirrors":["http://plain/x.zip"]}],"strategy":{"hardwareIds":[]}}}]}
            """;

        OemProtocol.ParseResponse(json)![0].Package!.Url.Should().BeNull("незашифрованную ссылку не предлагаем");
    }

    // ---- сверка ----

    private static readonly InstalledDriver Npu = new(@"PCI\VEN_8086&DEV_B03E&SUBSYS_24241D72&REV_04", "32.0.100.4900", "Intel AI Boost");
    private static readonly InstalledDriver Wifi = new(@"PCI\VEN_8086&DEV_06F0&SUBSYS_02748086", "24.10.0.4", "Intel Wi-Fi");
    private static readonly InstalledDriver Audio = new(@"INTELAUDIO\CTLR_DEV_EXT_4D28&LINKTYPE_02&VEN_8086", "20.43.12000.0", "Intel SST");

    private static OemModule Module(string name, params (string Id, string Version)[] ids) =>
        new(name, new OemPackage(name + ".zip", 1, "md5", "https://cdn/" + name), ids.Select(i => new OemHardwareId(i.Id, i.Version)).ToList());

    [Theory]
    [InlineData(@"PCI\VEN_8086&DEV_B03E", true)]
    [InlineData(@"pci\ven_8086&dev_b03e", true)]                   // регистр не важен
    [InlineData(@"PCI\VEN_8086&DEV_B03", false)]                   // префикс не по границе сегмента
    [InlineData(@"PCI\VEN_8086&DEV_B03E&SUBSYS_24241D72&REV_04", true)]
    [InlineData(@"PCI\VEN_8086&DEV_B03E&SUBSYS_00000000", false)]
    public void Префикс_hardware_id_по_границе_сегмента(string catalogId, bool found) =>
        (DriverMatch.Find([Npu], catalogId) is not null).Should().Be(found);

    [Theory]
    [InlineData("32.0.101.8359", "32.0.101.8136", 1)]
    [InlineData("10.1.56.27", "10.1.56.27", 0)]
    [InlineData("24.10.0.4", "24.100.0.1", -1)] // числами, а не строкой: 10 < 100
    public void Версии_сравниваются_числами(string a, string b, int sign) =>
        Math.Sign(DriverMatch.CompareVersions(a, b)).Should().Be(sign);

    [Fact]
    public void Сверка_даёт_обновление_совпадение_и_новее()
    {
        var rows = DriverMatch.Compare(
            [
                Module("NPU", (@"PCI\VEN_8086&DEV_B03E", "32.0.100.4841")),           // у нас новее
                Module("WLC", (@"PCI\VEN_8086&DEV_06F0", "24.10.0.4")),               // совпало
                Module("SC", (@"SWC\VEN_DOLBY&PID_DAX3HSA_DolbyAtmos", "3.30900.900.0"), // такого устройства нет
                             (@"INTELAUDIO\CTLR_DEV_EXT_4D28&LINKTYPE_02&VEN_8086", "20.43.12573.0")),
            ],
            [Npu, Wifi, Audio]);

        rows.Select(r => (r.Module, r.State)).Should().BeEquivalentTo(
            [("NPU", DriverState.Newer), ("WLC", DriverState.Same), ("SC", DriverState.Update)]);
        rows.Single(r => r.Module == "SC").Catalog.Should().Be("20.43.12573.0",
            "версия — из записи этого hardware-id, а не первая в пакете (у Dolby своя)");
    }

    [Fact]
    public void Одно_устройство_под_несколькими_id_каталога_одной_строкой()
    {
        var rows = DriverMatch.Compare(
            [Module("WLC", (@"PCI\VEN_8086&DEV_06F0", "24.10.0.4"), (@"PCI\VEN_8086&DEV_06F0&SUBSYS_02748086", "24.10.0.4"))],
            [Wifi]);

        rows.Should().ContainSingle();
    }

    [Fact]
    public void Прошивку_с_версией_драйвера_не_сравниваем()
    {
        var touchpad = new InstalledDriver(@"USB\VID_6989&PID_1234&REV_1836", "10.0.26100.1", "Touchpad");

        var rows = DriverMatch.Compare([Module("FWTP", (@"USB\VID_6989&PID_1234&REV_1836", "3.5.13.01.44.25.13.0"))], [touchpad]);

        rows.Should().ContainSingle().Which.Should().Match<DriverRow>(r => r.State == DriverState.Firmware && r.Installed == null);
    }

    // ---- прошивки без устройства (XIC-102) ----

    [Fact]
    public void Прошивка_без_совпавшего_устройства_даёт_одну_строку_на_модуль()
    {
        // id прошивок в каталоге синтетические — в системе их нет; устройства подставлены настоящие с TM2424
        var rows = DriverMatch.Compare(
            [
                Module("FWSC", (@"USB\VID_6789&PID_1234&REV_1236", "740B08"), (@"USB\VID_6789&PID_1234&REV_1237", "740B08")),
                Module("FWTP", (@"USB\VID_6989&PID_1234&REV_1836", "3.5.13.01.44.25.13.0"), (@"USB\VID_6989&PID_1234&REV_1837", "3.5.13.01.44.25.13.0")),
            ],
            [Npu, Wifi, Audio]);

        rows.Should().HaveCount(2, "по строке на модуль, а не на каждый hardware-id");
        rows.Should().AllSatisfy(r =>
        {
            r.State.Should().Be(DriverState.Firmware);
            r.Installed.Should().BeNull("текущую версию прошивки читать нечем");
            r.Device.Should().Be(r.Module, "имя устройства даёт UI по коду модуля, не PnP");
            r.Package.Should().NotBeNull();
        });
        rows.Single(r => r.Module == "FWSC").Catalog.Should().Be("740B08");
        rows.Single(r => r.Module == "FWTP").Catalog.Should().Be("44.25.13.0", "у тачпада показываем саму версию прошивки, без номера пакета");
    }

    // ---- текущие версии прошивок (XIC-104) ----

    [Theory]
    [InlineData("3.5.13.01.44.25.13.0", "44.25.13.0")]
    [InlineData("44.25.13.0", "44.25.13.0")]
    [InlineData("44.25.13", null)]
    [InlineData("3.5.13.01.44.25.13.x", null)]
    [InlineData("740B08", null)]
    [InlineData("", null)]
    public void Версия_тачпада_из_хвоста_каталожной(string catalog, string? expected) =>
        DriverMatch.TouchpadCatalogVersion(catalog).Should().Be(expected);

    private static readonly OemModule Touchpad = Module("FWTP", (@"USB\VID_6989&PID_1234&REV_1836", "3.5.13.01.44.25.13.0"));
    private static readonly OemModule Panel = Module("FWSC", (@"USB\VID_6789&PID_1234&REV_1236", "740B08"));

    [Theory]
    [InlineData("44.25.11.0", DriverState.Update)] // TM2424 на момент XIC-103
    [InlineData("44.25.13.0", DriverState.Same)]
    [InlineData("44.25.14.0", DriverState.Newer)]
    public void Тачпад_с_прочитанной_версией_сравнивается_как_драйвер(string current, DriverState state)
    {
        var row = DriverMatch.Compare([Touchpad], [], new Dictionary<string, string> { ["FWTP"] = current }).Single();

        row.State.Should().Be(state);
        row.Installed.Should().Be(current);
        row.Catalog.Should().Be("44.25.13.0");
        row.Package.Should().NotBeNull();
    }

    [Fact]
    public void Экран_с_прочитанной_версией_только_рядом_без_вывода()
    {
        var row = DriverMatch.Compare([Panel], [], new Dictionary<string, string> { ["FWSC"] = "570B06" }).Single();

        row.State.Should().Be(DriverState.Firmware, "старший байт ROM Ver отличается — может быть другой вариант панели");
        row.Installed.Should().Be("570B06");
        row.Catalog.Should().Be("740B08");
    }

    [Fact]
    public void Не_прочиталась_версия_остаётся_в_каталоге_есть()
    {
        var rows = DriverMatch.Compare([Touchpad, Panel], [], new Dictionary<string, string>());

        rows.Should().HaveCount(2).And.AllSatisfy(r =>
        {
            r.State.Should().Be(DriverState.Firmware);
            r.Installed.Should().BeNull();
        });
    }

    [Fact]
    public void Прошивка_без_пакета_строки_не_даёт()
    {
        var noPackage = new OemModule("FWSC", null, [new OemHardwareId(@"USB\VID_6789&PID_1234&REV_1236", "740B08")]);

        DriverMatch.Compare([noPackage], []).Should().BeEmpty("нечего предлагать — ни ссылки, ни пакета");
    }

    [Fact]
    public void Драйвер_без_совпавшего_устройства_строки_не_даёт()
    {
        var rows = DriverMatch.Compare([Module("NFC", (@"PCI\VEN_1FC9&DEV_0001", "1.0.0.1"))], [Npu, Wifi, Audio]);

        rows.Should().BeEmpty("для обычных драйверов правило прежнее: нет устройства — нет строки");
    }
}
