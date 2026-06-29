using System.Threading.Tasks;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

public class HibernationStateTests
{
    [Fact]
    public void Present_One_IsEnabled_OffersOnlyDisable()
    {
        var s = new HibernationState("1", IsPresent: true);
        Assert.True(s.IsEnabled);
        Assert.False(s.IsDisabled);
        Assert.False(s.IsUnknown);
        Assert.False(s.CanEnable);   // already enabled → no no-op button
        Assert.True(s.CanDisable);
        Assert.Equal("Activée", s.StateDisplay);
    }

    [Fact]
    public void Present_Zero_IsDisabled_OffersOnlyEnable()
    {
        var s = new HibernationState("0", IsPresent: true);
        Assert.True(s.IsDisabled);
        Assert.False(s.IsEnabled);
        Assert.False(s.IsUnknown);
        Assert.True(s.CanEnable);
        Assert.False(s.CanDisable);  // already disabled → no no-op button
        Assert.Equal("Désactivée", s.StateDisplay);
    }

    [Fact]
    public void Absent_IsUnknown_OffersBothActions()
    {
        // The platform default genuinely varies, so an absent value is never reported as a fabricated on/off —
        // both actions are offered because powercfg /hibernate on|off is a legitimate way to force a known state.
        var s = new HibernationState(null, IsPresent: false);
        Assert.True(s.IsUnknown);
        Assert.False(s.IsEnabled);
        Assert.False(s.IsDisabled);
        Assert.True(s.CanEnable);
        Assert.True(s.CanDisable);
        Assert.Equal("Inconnu", s.StateDisplay);
    }

    [Theory]
    [InlineData("0x1", true)]
    [InlineData("0x0", false)]
    public void HexDword_ComparesNumerically(string value, bool expectedEnabled)
    {
        var s = new HibernationState(value, IsPresent: true);
        Assert.Equal(expectedEnabled, s.IsEnabled);
        Assert.Equal(!expectedEnabled, s.IsDisabled);
    }
}

public class FastStartupStateTests
{
    [Fact]
    public void HibernationOff_IsUnavailable_NoActions()
    {
        // Fast Startup writes a hiberfile, so it is meaningless while hibernation is off — reported honestly as
        // unavailable with no toggle, rather than pretending a change would take effect.
        var s = new FastStartupState("1", IsPresent: true, HibernationEnabled: false);
        Assert.False(s.IsAvailable);
        Assert.False(s.IsEnabled);
        Assert.False(s.IsDisabled);
        Assert.False(s.CanEnable);
        Assert.False(s.CanDisable);
        Assert.Equal("Indisponible (hibernation désactivée)", s.StateDisplay);
    }

    [Fact]
    public void HibernationOn_Absent_DefaultsToEnabled()
    {
        // Windows' documented default on a clean consumer install is ON, so an absent value reads as enabled-by-default.
        var s = new FastStartupState(null, IsPresent: false, HibernationEnabled: true);
        Assert.True(s.IsAvailable);
        Assert.True(s.IsEnabled);
        Assert.False(s.IsDisabled);
        Assert.False(s.CanEnable);
        Assert.True(s.CanDisable);
        Assert.Equal("Activé (défaut Windows)", s.StateDisplay);
    }

    [Fact]
    public void HibernationOn_PresentOne_IsEnabled()
    {
        var s = new FastStartupState("1", IsPresent: true, HibernationEnabled: true);
        Assert.True(s.IsEnabled);
        Assert.False(s.IsDisabled);
        Assert.False(s.CanEnable);
        Assert.True(s.CanDisable);
        Assert.Equal("Activé", s.StateDisplay);
    }

    [Fact]
    public void HibernationOn_PresentZero_IsDisabled()
    {
        var s = new FastStartupState("0", IsPresent: true, HibernationEnabled: true);
        Assert.True(s.IsDisabled);
        Assert.False(s.IsEnabled);
        Assert.True(s.CanEnable);
        Assert.False(s.CanDisable);
        Assert.Equal("Désactivé", s.StateDisplay);
    }

    [Theory]
    [InlineData("0x0", false)]
    [InlineData("0x1", true)]
    public void HexDword_ComparesNumerically(string value, bool expectedEnabled)
    {
        var s = new FastStartupState(value, IsPresent: true, HibernationEnabled: true);
        Assert.Equal(expectedEnabled, s.IsEnabled);
        Assert.Equal(!expectedEnabled, s.IsDisabled);
    }
}

public class PowercfgHibernateCommandTests
{
    // Load-bearing: a wrong argument here would silently fire the wrong command against the OS.
    [Fact]
    public void Build_Enable_IsHibernateOn()
    {
        var (file, args) = PowercfgHibernateCommand.Build(enable: true);
        Assert.Equal("powercfg.exe", file);
        Assert.Equal("/hibernate on", args);
    }

    [Fact]
    public void Build_Disable_IsHibernateOff()
    {
        var (file, args) = PowercfgHibernateCommand.Build(enable: false);
        Assert.Equal("powercfg.exe", file);
        Assert.Equal("/hibernate off", args);
    }
}

public class SleepHibernationReportTests
{
    private static SleepHibernationReport Report(HibernationState hib, long? bytes) =>
        new(hib, new FastStartupState(null, false, hib.IsEnabled), bytes);

    [Fact]
    public void EnabledWithHiberfil_HeadlineShowsSize()
    {
        var rep = Report(new HibernationState("1", true), 8_000_000_000L);
        Assert.True(rep.HasHiberfil);
        Assert.NotEqual("—", rep.HiberfilDisplay);
        Assert.Contains("hiberfil.sys", rep.Headline);
    }

    [Fact]
    public void EnabledWithoutMeasurableHiberfil_NoFakeZero()
    {
        var rep = Report(new HibernationState("1", true), null);
        Assert.False(rep.HasHiberfil);
        Assert.Equal("—", rep.HiberfilDisplay);
        Assert.Equal("Hibernation activée", rep.Headline);
    }

    [Fact]
    public void ZeroBytes_TreatedAsNotMeasurable()
    {
        var rep = Report(new HibernationState("1", true), 0L);
        Assert.False(rep.HasHiberfil);
        Assert.Equal("—", rep.HiberfilDisplay);
    }

    [Fact]
    public void Disabled_HeadlineMentionsReclaimedSpace()
    {
        var rep = Report(new HibernationState("0", true), null);
        Assert.Contains("désactivée", rep.Headline);
    }

    [Fact]
    public void Unknown_HeadlineIsHonest()
    {
        var rep = Report(new HibernationState(null, false), null);
        Assert.Equal("État de l'hibernation inconnu", rep.Headline);
    }
}

public class SleepHibernationServiceTests
{
    private static SleepHibernationService NewService(out FakeRegistryService reg)
    {
        reg = new FakeRegistryService(new EventLog());
        return new SleepHibernationService(reg);
    }

    [Fact]
    public async Task GetReport_ReadsBothStatesFromRegistry()
    {
        var svc = NewService(out var reg);
        reg.Seed("HKLM", SleepHibernationService.PowerKey, SleepHibernationService.HibernateValue, "1");
        reg.Seed("HKLM", SleepHibernationService.SessionPowerKey, SleepHibernationService.FastStartupValue, "0");

        var rep = await svc.GetReportAsync();

        Assert.True(rep.Hibernation.IsEnabled);
        Assert.True(rep.FastStartup.IsAvailable);
        Assert.True(rep.FastStartup.IsDisabled);
    }

    [Fact]
    public async Task GetReport_AbsentHibernation_IsUnknown_FastStartupUnavailable()
    {
        var svc = NewService(out _);

        var rep = await svc.GetReportAsync();

        Assert.True(rep.Hibernation.IsUnknown);
        Assert.False(rep.FastStartup.IsAvailable);
    }

    [Fact]
    public async Task GetReport_HibernationOff_MakesFastStartupUnavailable_EvenIfHiberbootSeededOn()
    {
        var svc = NewService(out var reg);
        reg.Seed("HKLM", SleepHibernationService.PowerKey, SleepHibernationService.HibernateValue, "0");
        reg.Seed("HKLM", SleepHibernationService.SessionPowerKey, SleepHibernationService.FastStartupValue, "1");

        var rep = await svc.GetReportAsync();

        Assert.True(rep.Hibernation.IsDisabled);
        Assert.False(rep.FastStartup.IsAvailable);
    }

    [Fact]
    public async Task SetFastStartup_True_WritesOne()
    {
        var svc = NewService(out var reg);

        var ok = await svc.SetFastStartupAsync(enable: true);

        Assert.True(ok);
        Assert.True(reg.TryReadValue("HKLM", SleepHibernationService.SessionPowerKey, SleepHibernationService.FastStartupValue, out var v));
        Assert.Equal("1", v);
    }

    [Fact]
    public async Task SetFastStartup_False_WritesZero()
    {
        var svc = NewService(out var reg);

        var ok = await svc.SetFastStartupAsync(enable: false);

        Assert.True(ok);
        Assert.True(reg.TryReadValue("HKLM", SleepHibernationService.SessionPowerKey, SleepHibernationService.FastStartupValue, out var v));
        Assert.Equal("0", v);
    }
}
