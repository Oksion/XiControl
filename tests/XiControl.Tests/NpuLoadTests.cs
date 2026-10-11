using FluentAssertions;
using XiControl.SystemIntegration;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// NpuLoad.Aggregate (XIC-86): экземпляры «GPU Engine» → одна загрузка NPU, как в диспетчере
/// задач. Живой PDH юнитами не трогаем — только сведение. Имена — в формате Windows:
/// pid_N_luid_..._phys_N_eng_N_engtype_X.
/// </summary>
public sealed class NpuLoadTests
{
    private static (string, double) I(int pid, int eng, string type, double v) =>
        ($"pid_{pid}_luid_0x00000000_0x0000D1A4_phys_0_eng_{eng}_engtype_{type}", v);

    [Fact]
    public void Учитываются_только_движки_Neural()
    {
        NpuLoad.Aggregate([I(10, 0, "3D", 90), I(10, 1, "Compute", 70), I(10, 2, "Neural", 12)])
            .Should().Be(12f);
    }

    [Fact]
    public void Процессы_на_одном_движке_складываются()
    {
        NpuLoad.Aggregate([I(10, 0, "Neural", 20), I(11, 0, "Neural", 15)])
            .Should().Be(35f);
    }

    [Fact]
    public void Между_движками_берётся_максимум_а_не_сумма()
    {
        // Форк складывал всё подряд: две половины работы на двух движках давали «100%»
        NpuLoad.Aggregate([I(10, 0, "Neural", 50), I(11, 1, "Neural", 50)])
            .Should().Be(50f);
        NpuLoad.Aggregate([I(10, 0, "Neural", 30), I(11, 0, "Neural", 10), I(12, 1, "Neural", 25)])
            .Should().Be(40f);
    }

    [Fact]
    public void Без_работы_ноль_а_не_прочерк()
    {
        NpuLoad.Aggregate([]).Should().Be(0f);
        NpuLoad.Aggregate([I(10, 0, "3D", 40)]).Should().Be(0f);
        NpuLoad.Aggregate([I(10, 0, "Neural", 0)]).Should().Be(0f);
    }

    [Fact]
    public void Выше_100_не_бывает()
    {
        NpuLoad.Aggregate([I(10, 0, "Neural", 80), I(11, 0, "Neural", 45)])
            .Should().Be(100f);
    }

    [Fact]
    public void Регистр_типа_движка_не_важен() =>
        NpuLoad.Aggregate([("pid_1_luid_0x0_0x1_phys_0_eng_0_ENGTYPE_NEURAL", 7)])
            .Should().Be(7f);
}
