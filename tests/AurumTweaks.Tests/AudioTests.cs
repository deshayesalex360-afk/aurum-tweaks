using System;
using System.Threading.Tasks;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

public class AudioDuckingInfoTests
{
    [Theory]
    [InlineData(AudioDucking.ReduceOther80, "Réduire de 80 % le volume des autres sons")]
    [InlineData(AudioDucking.ReduceOther50, "Réduire de 50 % le volume des autres sons")]
    [InlineData(AudioDucking.MuteOthers,    "Couper tous les autres sons")]
    [InlineData(AudioDucking.DoNothing,     "Ne rien faire")]
    [InlineData(AudioDucking.Unknown,       "Indéterminé")]
    public void Describe_Cases(AudioDucking d, string expected)
        => Assert.Equal(expected, AudioDuckingInfo.Describe(d));

    [Theory]
    [InlineData("0", AudioDucking.ReduceOther80)]
    [InlineData("1", AudioDucking.ReduceOther50)]
    [InlineData("2", AudioDucking.MuteOthers)]
    [InlineData("3", AudioDucking.DoNothing)]
    [InlineData("0x3", AudioDucking.DoNothing)]   // regedit-style hex must classify the same as decimal
    [InlineData("7", AudioDucking.Unknown)]       // out-of-range but numeric → honestly unrecognised
    [InlineData("abc", AudioDucking.Unknown)]
    [InlineData("", AudioDucking.Unknown)]
    [InlineData(null, AudioDucking.Unknown)]
    public void Parse_Cases(string? raw, AudioDucking expected)
        => Assert.Equal(expected, AudioDuckingInfo.Parse(raw));

    [Theory]
    [InlineData(AudioDucking.ReduceOther80, "0")]
    [InlineData(AudioDucking.ReduceOther50, "1")]
    [InlineData(AudioDucking.MuteOthers,    "2")]
    [InlineData(AudioDucking.DoNothing,     "3")]
    public void ToRegistryValue_Cases(AudioDucking d, string expected)
        => Assert.Equal(expected, AudioDuckingInfo.ToRegistryValue(d));

    // The displayed mode and the value we write must never drift apart.
    [Theory]
    [InlineData(AudioDucking.ReduceOther80)]
    [InlineData(AudioDucking.ReduceOther50)]
    [InlineData(AudioDucking.MuteOthers)]
    [InlineData(AudioDucking.DoNothing)]
    public void RoundTrip_ValueToEnum(AudioDucking d)
        => Assert.Equal(d, AudioDuckingInfo.Parse(AudioDuckingInfo.ToRegistryValue(d)));
}

public class AudioDuckingAdvisorTests
{
    [Fact]
    public void DoNothing_IsOk_AndRecommended()
    {
        var r = AudioDuckingAdvisor.Assess(AudioDucking.DoNothing);
        Assert.Equal(AudioVerdict.Ok, r.Verdict);
        Assert.Contains("recommandé", r.Headline);
    }

    // Load-bearing honesty: muting all is WARNED because it silences the game too.
    [Fact]
    public void MuteOthers_IsWarning_AndHonest()
    {
        var r = AudioDuckingAdvisor.Assess(AudioDucking.MuteOthers);
        Assert.Equal(AudioVerdict.Warning, r.Verdict);
        Assert.Contains("déconseillé", r.Headline);
        Assert.Contains("coupé", r.Detail);
    }

    [Theory]
    [InlineData(AudioDucking.ReduceOther80)]
    [InlineData(AudioDucking.ReduceOther50)]
    public void Reduce_IsInfo(AudioDucking d)
        => Assert.Equal(AudioVerdict.Info, AudioDuckingAdvisor.Assess(d).Verdict);

    [Fact]
    public void Unknown_IsInfo_AndAdmitsItCannotRead()
    {
        var r = AudioDuckingAdvisor.Assess(AudioDucking.Unknown);
        Assert.Equal(AudioVerdict.Info, r.Verdict);
        Assert.Contains("indéterminée", r.Headline);
    }
}

public class AudioActionOutcomeTests
{
    // Verified true: the re-read equals what we asked for, and the message names the applied setting.
    [Fact]
    public void Verified_Equal_IsOk_AndNamesSetting()
    {
        var o = AudioActionOutcome.FromVerified(AudioDucking.DoNothing, AudioDucking.DoNothing);
        Assert.True(o.Ok);
        Assert.Contains("Ne rien faire", o.Message);
    }

    // Verified false: a silently-refused write (read-back differs) reports failure, never a fabricated success.
    [Fact]
    public void Verified_Differs_IsFailure()
    {
        var o = AudioActionOutcome.FromVerified(AudioDucking.MuteOthers, AudioDucking.DoNothing);
        Assert.False(o.Ok);
        Assert.Contains("refusée", o.Message);
    }

    [Fact]
    public void Failed_IsNotOk()
        => Assert.False(AudioActionOutcome.Failed.Ok);
}

public class AudioSoundSchemeTests
{
    [Theory]
    [InlineData(".None",     "Aucun son")]
    [InlineData(".Default",  "Windows par défaut")]
    [InlineData(".MonModele", "MonModele")]
    [InlineData("", "Indéterminé")]
    [InlineData(null, "Indéterminé")]
    public void Describe_Cases(string? raw, string expected)
        => Assert.Equal(expected, AudioSoundScheme.Describe(raw));

    [Theory]
    [InlineData(".None", true)]
    [InlineData(".Default", false)]
    [InlineData(null, false)]
    public void IsSilent_OnlyForNone(string? raw, bool expected)
        => Assert.Equal(expected, AudioSoundScheme.IsSilent(raw));
}

public class AudioDeviceTests
{
    [Fact]
    public void Blanks_Dash_AndFallbackName()
    {
        var d = new AudioDevice("", "", "");
        Assert.Equal("Périphérique audio", d.NameDisplay);
        Assert.Equal("—", d.ManufacturerDisplay);
        Assert.Equal("—", d.StatusDisplay);
        Assert.False(d.IsOk);
    }

    [Theory]
    [InlineData("OK", true)]
    [InlineData("Error", false)]
    [InlineData("", false)]
    public void IsOk_OnlyWhenStatusOk(string status, bool expected)
        => Assert.Equal(expected, new AudioDevice("Realtek", "Realtek", status).IsOk);
}

public class AudioReportTests
{
    // Absent value: Windows' implicit default is "reduce 80 %" — reported honestly, the recommended write still offered,
    // and "restore default" hidden (it would be a no-op against the effective default).
    [Fact]
    public void Absent_IsImplicitDefault_OffersApply_HidesRestore()
    {
        var rep = AudioReport.From(readOk: false, duckingRaw: null);
        Assert.False(rep.IsExplicit);
        Assert.Equal(AudioDucking.ReduceOther80, rep.Ducking);
        Assert.True(rep.VerdictInfo);
        Assert.True(rep.CanApplyRecommended);
        Assert.False(rep.CanRestoreDefault);
        Assert.False(rep.IsRecommended);
        Assert.Contains("défaut", rep.Headline);
        Assert.Contains("80 %", rep.Headline);
    }

    // Explicit default 80 %: applying "Ne rien faire" still does something; restoring the default would be a no-op (hidden).
    [Fact]
    public void Explicit80_OffersApply_HidesRestore()
    {
        var rep = AudioReport.From(readOk: true, duckingRaw: "0");
        Assert.True(rep.IsExplicit);
        Assert.Equal(AudioDucking.ReduceOther80, rep.Ducking);
        Assert.True(rep.VerdictInfo);
        Assert.True(rep.CanApplyRecommended);
        Assert.False(rep.CanRestoreDefault);
    }

    [Fact]
    public void Reduce50_OffersBothWrites_Info()
    {
        var rep = AudioReport.From(readOk: true, duckingRaw: "1");
        Assert.Equal(AudioDucking.ReduceOther50, rep.Ducking);
        Assert.True(rep.VerdictInfo);
        Assert.True(rep.CanApplyRecommended);
        Assert.True(rep.CanRestoreDefault);
    }

    [Fact]
    public void MuteOthers_IsWarning_OffersBothWrites()
    {
        var rep = AudioReport.From(readOk: true, duckingRaw: "2");
        Assert.Equal(AudioDucking.MuteOthers, rep.Ducking);
        Assert.True(rep.VerdictWarn);
        Assert.True(rep.CanApplyRecommended);
        Assert.True(rep.CanRestoreDefault);
    }

    // Already "Ne rien faire": recommended state — the apply button is a no-op (hidden), restore-default is offered.
    [Fact]
    public void DoNothing_IsRecommended_HidesApply_OffersRestore()
    {
        var rep = AudioReport.From(readOk: true, duckingRaw: "3");
        Assert.Equal(AudioDucking.DoNothing, rep.Ducking);
        Assert.True(rep.VerdictOk);
        Assert.True(rep.IsRecommended);
        Assert.False(rep.CanApplyRecommended);
        Assert.True(rep.CanRestoreDefault);
    }

    // Present-but-garbage: honestly Unknown — offer the recommended known state, but don't pretend "restore default" applies.
    [Fact]
    public void Garbage_IsUnknown_OffersApply_HidesRestore()
    {
        var rep = AudioReport.From(readOk: true, duckingRaw: "7");
        Assert.Equal(AudioDucking.Unknown, rep.Ducking);
        Assert.True(rep.VerdictInfo);
        Assert.True(rep.CanApplyRecommended);
        Assert.False(rep.CanRestoreDefault);
    }

    [Fact]
    public void Scheme_PassesThrough()
    {
        var rep = AudioReport.From(readOk: true, duckingRaw: "3", schemeRaw: ".None");
        Assert.Equal("Aucun son", rep.SchemeDisplay);
        Assert.True(rep.SystemSoundsSilent);
    }

    [Fact]
    public void Devices_PassThrough_AndCount()
    {
        var rep = AudioReport.From(readOk: true, duckingRaw: "3", devices: new[]
        {
            new AudioDevice("Realtek HD Audio", "Realtek", "OK"),
            new AudioDevice("NVIDIA HDMI", "NVIDIA", "OK")
        });
        Assert.True(rep.HasDevices);
        Assert.Equal(2, rep.DeviceCount);
    }

    [Fact]
    public void Failed_IsHonest_OffersApply()
    {
        var rep = AudioReport.Failed;
        Assert.Equal(AudioDucking.Unknown, rep.Ducking);
        Assert.True(rep.VerdictInfo);
        Assert.Contains("impossible", rep.Headline);
        Assert.True(rep.CanApplyRecommended);
        Assert.False(rep.CanRestoreDefault);
        Assert.False(rep.HasDevices);
    }
}

// Service-level: registry-only path (SetDucking), driven by the in-memory FakeRegistryService — never touches WMI.
public class AudioServiceTests
{
    private const string Hive = "HKCU";
    private const string Key = @"Software\Microsoft\Multimedia\Audio";
    private const string Name = "UserDuckingPreference";

    [Fact]
    public async Task SetDucking_DoNothing_Writes3_AndVerifies()
    {
        var log = new EventLog();
        var reg = new FakeRegistryService(log);
        var svc = new AudioService(reg);

        var outcome = await svc.SetDuckingAsync(AudioDucking.DoNothing);

        Assert.True(outcome.Ok);
        Assert.True(reg.TryReadValue(Hive, Key, Name, out var raw));
        Assert.Equal("3", raw);
    }

    // Reversibility: the same path restores the Windows default (0) and verifies it.
    [Fact]
    public async Task SetDucking_RestoreDefault_Writes0_AndVerifies()
    {
        var log = new EventLog();
        var reg = new FakeRegistryService(log);
        reg.Seed(Hive, Key, Name, "3");   // start from "Ne rien faire"
        var svc = new AudioService(reg);

        var outcome = await svc.SetDuckingAsync(AudioDucking.ReduceOther80);

        Assert.True(outcome.Ok);
        Assert.True(reg.TryReadValue(Hive, Key, Name, out var raw));
        Assert.Equal("0", raw);
    }

    // A backend that refuses the write reports failure honestly — never a fabricated success.
    [Fact]
    public async Task SetDucking_WriteRefused_IsFailure()
    {
        var log = new EventLog();
        var reg = new FakeRegistryService(log);
        reg.FailWritesForName.Add(Name);
        var svc = new AudioService(reg);

        var outcome = await svc.SetDuckingAsync(AudioDucking.DoNothing);

        Assert.False(outcome.Ok);
    }

    // The Unknown sentinel is never written — it would be a meaningless DWORD.
    [Fact]
    public async Task SetDucking_Unknown_IsFailure_AndWritesNothing()
    {
        var log = new EventLog();
        var reg = new FakeRegistryService(log);
        var svc = new AudioService(reg);

        var outcome = await svc.SetDuckingAsync(AudioDucking.Unknown);

        Assert.False(outcome.Ok);
        Assert.False(reg.TryReadValue(Hive, Key, Name, out _));
        Assert.DoesNotContain(log.Events, e => e.StartsWith("reg.write"));
    }
}
