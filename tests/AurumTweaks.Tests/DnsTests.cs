using System;
using System.Collections.Generic;
using System.Linq;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Builders for the DNS pure-core tests. The service's WMI/registry calls are untested glue ("test the decision,
/// not the world") — everything pinned here is the honesty-bearing logic: the preset catalog, address parsing,
/// static-vs-automatic classification, no-op gating, the MEASURED-by-re-read outcome, and the report headline.
/// </summary>
internal static class DnsFixtures
{
    public static DnsPreset Cloudflare => DnsPresetCatalog.Find("Cloudflare")!;
    public static DnsPreset Google => DnsPresetCatalog.Find("Google Public DNS")!;

    public static DnsAdapterState Adapter(
        DnsMode mode,
        IReadOnlyList<string>? staticServers = null,
        IReadOnlyList<string>? effective = null,
        bool connected = true,
        string description = "Intel Ethernet",
        string settingId = "{GUID-1}") =>
        new(description, settingId, connected, mode,
            effective ?? new[] { "1.1.1.1" },
            staticServers ?? Array.Empty<string>());
}

public class DnsPresetTests
{
    [Fact]
    public void WithSecondary_ExposesBothAddresses()
    {
        var p = new DnsPreset("X", "1.1.1.1", "1.0.0.1", "d");
        Assert.Equal(new[] { "1.1.1.1", "1.0.0.1" }, p.Addresses);
        Assert.Equal("1.1.1.1 · 1.0.0.1", p.ServersDisplay);
    }

    [Fact]
    public void WithoutSecondary_ExposesOneAddress()
    {
        var p = new DnsPreset("X", "9.9.9.9", null, "d");
        Assert.Single(p.Addresses);
        Assert.Equal("9.9.9.9", p.ServersDisplay);
    }

    [Fact]
    public void BlankSecondary_TreatedAsNone()
    {
        var p = new DnsPreset("X", "9.9.9.9", "   ", "d");
        Assert.Single(p.Addresses);
        Assert.Equal("9.9.9.9", p.ServersDisplay);
    }
}

public class DnsPresetCatalogTests
{
    [Fact]
    public void Presets_NotEmpty_WithUniqueNames()
    {
        Assert.NotEmpty(DnsPresetCatalog.Presets);
        Assert.Equal(DnsPresetCatalog.Presets.Count,
                     DnsPresetCatalog.Presets.Select(p => p.Name).Distinct().Count());
    }

    [Fact]
    public void EveryPreset_HasPrimaryAddressAndDescription()
    {
        Assert.All(DnsPresetCatalog.Presets, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Primary));
            Assert.False(string.IsNullOrWhiteSpace(p.Description));
            Assert.NotEmpty(p.Addresses);
        });
    }

    // Load-bearing: these addresses are written to a real adapter — they must never silently drift to wrong values.
    [Theory]
    [InlineData("Cloudflare", "1.1.1.1", "1.0.0.1")]
    [InlineData("Google Public DNS", "8.8.8.8", "8.8.4.4")]
    [InlineData("Quad9", "9.9.9.9", "149.112.112.112")]
    [InlineData("OpenDNS", "208.67.222.222", "208.67.220.220")]
    [InlineData("AdGuard DNS", "94.140.14.14", "94.140.15.15")]
    public void PinsKnownProviderAddresses(string name, string primary, string secondary)
    {
        var p = DnsPresetCatalog.Find(name);
        Assert.NotNull(p);
        Assert.Equal(primary, p!.Primary);
        Assert.Equal(secondary, p.Secondary);
    }

    [Fact]
    public void Find_IsCaseInsensitive() => Assert.NotNull(DnsPresetCatalog.Find("cLoUdFlArE"));

    [Theory]
    [InlineData("")]
    [InlineData("Nonexistent")]
    public void Find_Unknown_ReturnsNull(string name) => Assert.Null(DnsPresetCatalog.Find(name));

    // FindByPrimary is the bridge from a benchmark winner to an applicable preset. It must match every curated
    // resolver by its PRIMARY address — the join key that survives the deliberate name divergence below.
    [Theory]
    [InlineData("1.1.1.1", "Cloudflare")]
    [InlineData("8.8.8.8", "Google Public DNS")]
    [InlineData("9.9.9.9", "Quad9")]
    [InlineData("208.67.222.222", "OpenDNS")]
    [InlineData("94.140.14.14", "AdGuard DNS")]
    public void FindByPrimary_MatchesEachCuratedAddress(string primary, string expectedName)
        => Assert.Equal(expectedName, DnsPresetCatalog.FindByPrimary(primary)!.Name);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("203.0.113.9")]   // a valid IP that simply isn't one of ours
    public void FindByPrimary_BlankOrUnknown_ReturnsNull(string? primary)
        => Assert.Null(DnsPresetCatalog.FindByPrimary(primary));

    [Fact]
    public void FindByPrimary_TrimsAndIgnoresCase()
        => Assert.Equal("Cloudflare", DnsPresetCatalog.FindByPrimary("  1.1.1.1  ")!.Name);

    // The load-bearing reason FindByPrimary exists: the benchmark names two providers differently from the catalog
    // ("Google" vs "Google Public DNS", "AdGuard" vs "AdGuard DNS"), so a by-NAME bridge would silently drop them —
    // but every benchmark resolver still resolves to a preset by ADDRESS.
    [Fact]
    public void FindByPrimary_BridgesBenchmarkResolversThatFailByName()
    {
        Assert.Null(DnsPresetCatalog.Find("Google"));                       // benchmark name ≠ catalog name
        Assert.Equal("Google Public DNS", DnsPresetCatalog.FindByPrimary("8.8.8.8")!.Name);

        Assert.Null(DnsPresetCatalog.Find("AdGuard"));
        Assert.Equal("AdGuard DNS", DnsPresetCatalog.FindByPrimary("94.140.14.14")!.Name);

        // And the whole benchmark roster maps cleanly by address — no winner can be left unbridgeable by name drift.
        Assert.All(DnsBenchmarkMath.DefaultResolvers,
                   r => Assert.NotNull(DnsPresetCatalog.FindByPrimary(r.Address)));
    }
}

public class DnsAddressesTests
{
    [Theory]
    [InlineData("1.1.1.1,1.0.0.1", 2)]
    [InlineData("1.1.1.1 1.0.0.1", 2)]
    [InlineData("1.1.1.1;1.0.0.1", 2)]
    [InlineData("1.1.1.1, 1.0.0.1", 2)]
    [InlineData("8.8.8.8", 1)]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData(null, 0)]
    public void Parse_SplitsAndDropsBlanks(string? raw, int count) =>
        Assert.Equal(count, DnsAddresses.Parse(raw).Count);

    [Fact]
    public void Parse_TrimsEntries()
    {
        var r = DnsAddresses.Parse("  1.1.1.1 ,  1.0.0.1  ");
        Assert.Equal(new[] { "1.1.1.1", "1.0.0.1" }, r);
    }

    [Fact]
    public void Equal_SameOrder_True() =>
        Assert.True(DnsAddresses.Equal(new[] { "1.1.1.1", "1.0.0.1" }, new[] { "1.1.1.1", "1.0.0.1" }));

    [Fact]
    public void Equal_DifferentOrder_False() =>
        Assert.False(DnsAddresses.Equal(new[] { "1.1.1.1", "1.0.0.1" }, new[] { "1.0.0.1", "1.1.1.1" }));

    [Fact]
    public void Equal_DifferentLength_False() =>
        Assert.False(DnsAddresses.Equal(new[] { "1.1.1.1" }, new[] { "1.1.1.1", "1.0.0.1" }));

    [Fact]
    public void Equal_CaseInsensitive_True() =>
        Assert.True(DnsAddresses.Equal(new[] { "FE80::1" }, new[] { "fe80::1" }));

    [Fact]
    public void Display_Empty_ReturnsDash() => Assert.Equal("—", DnsAddresses.Display(Array.Empty<string>()));

    [Fact]
    public void Display_JoinsWithMiddot() =>
        Assert.Equal("1.1.1.1 · 1.0.0.1", DnsAddresses.Display(new[] { "1.1.1.1", "1.0.0.1" }));
}

public class DnsModeClassifierTests
{
    [Theory]
    [InlineData(false, 0, DnsMode.Unknown)]   // no readable adapter id ⇒ never guessed
    [InlineData(false, 2, DnsMode.Unknown)]
    [InlineData(true, 0, DnsMode.Automatic)]  // empty static NameServer ⇒ DHCP
    [InlineData(true, 1, DnsMode.Static)]     // a static NameServer ⇒ manual
    [InlineData(true, 2, DnsMode.Static)]
    public void Classify_FollowsHonestRule(bool hasId, int staticCount, DnsMode expected) =>
        Assert.Equal(expected, DnsModeClassifier.Classify(hasId, staticCount));
}

public class DnsAdapterStateTests
{
    [Fact]
    public void Name_FallsBackWhenDescriptionBlank()
    {
        var a = DnsFixtures.Adapter(DnsMode.Automatic, description: "   ");
        Assert.Equal("Carte réseau", a.Name);
    }

    [Fact]
    public void Static_LabelsAndFlags()
    {
        var a = DnsFixtures.Adapter(DnsMode.Static, staticServers: new[] { "1.1.1.1" });
        Assert.True(a.IsStatic);
        Assert.False(a.IsAutomatic);
        Assert.Equal("Manuel (statique)", a.ModeLabel);
        Assert.Contains("manuellement", a.SourceNote);
    }

    [Fact]
    public void Automatic_LabelsAndFlags()
    {
        var a = DnsFixtures.Adapter(DnsMode.Automatic, effective: new[] { "1.1.1.1", "1.0.0.1" });
        Assert.True(a.IsAutomatic);
        Assert.Equal("Automatique (DHCP)", a.ModeLabel);
        Assert.Equal("1.1.1.1 · 1.0.0.1", a.EffectiveDisplay);
        Assert.Contains("DHCP", a.SourceNote);
    }

    [Fact]
    public void Unknown_HasDashEffectiveAndNoActions()
    {
        var a = DnsFixtures.Adapter(DnsMode.Unknown, settingId: "", effective: Array.Empty<string>());
        Assert.Equal("Indéterminé", a.ModeLabel);
        Assert.Equal("—", a.EffectiveDisplay);
        Assert.False(a.CanRevertToAutomatic);
        Assert.False(a.CanApply(DnsFixtures.Cloudflare));   // never act on an unreadable adapter
    }

    [Fact]
    public void StaticUsingPreset_OffersRevert_NotReapply()
    {
        var cf = DnsFixtures.Cloudflare;
        var a = DnsFixtures.Adapter(DnsMode.Static, staticServers: cf.Addresses.ToArray());
        Assert.True(a.IsUsingPreset(cf));
        Assert.False(a.CanApply(cf));            // already that preset → no-op gated
        Assert.True(a.CanRevertToAutomatic);
    }

    [Fact]
    public void StaticUsingOtherPreset_OffersSwitch()
    {
        var cf = DnsFixtures.Cloudflare;
        var a = DnsFixtures.Adapter(DnsMode.Static, staticServers: DnsFixtures.Google.Addresses.ToArray());
        Assert.False(a.IsUsingPreset(cf));
        Assert.True(a.CanApply(cf));             // switching providers is a real change
        Assert.True(a.CanRevertToAutomatic);
    }

    // The honesty distinction: DHCP happening to hand out a resolver's IPs is NOT "using" it (static config empty).
    [Fact]
    public void AutomaticWithMatchingEffective_NotUsingPreset_StillOffersApply()
    {
        var cf = DnsFixtures.Cloudflare;
        var a = DnsFixtures.Adapter(DnsMode.Automatic, staticServers: Array.Empty<string>(),
                                    effective: cf.Addresses.ToArray());
        Assert.False(a.IsUsingPreset(cf));        // static NameServer is empty → not pinned
        Assert.True(a.CanApply(cf));              // applying pins it static → a real change
        Assert.False(a.CanRevertToAutomatic);     // already automatic → revert is a no-op
    }
}

public class DnsApplyOutcomeTests
{
    private static readonly string[] Pair = { "1.1.1.1", "1.0.0.1" };

    [Fact]
    public void Apply_MeasuredMatch_IsVerified()
    {
        var o = new DnsApplyOutcome("Ethernet", false, Pair, Pair, DnsApplyStatus.Succeeded);
        Assert.True(o.Verified);
        Assert.Contains("vérifié", o.Summary);
    }

    [Fact]
    public void Apply_MeasuredMismatch_NotVerified_ReportsMeasured()
    {
        var o = new DnsApplyOutcome("Ethernet", false, Pair, new[] { "8.8.8.8" }, DnsApplyStatus.Succeeded);
        Assert.False(o.Verified);
        Assert.Contains("diffère", o.Summary);
        Assert.Contains("8.8.8.8", o.Summary);
    }

    [Fact]
    public void Revert_MeasuredEmpty_IsVerified()
    {
        var o = new DnsApplyOutcome("Ethernet", true, Array.Empty<string>(), Array.Empty<string>(), DnsApplyStatus.Succeeded);
        Assert.True(o.Verified);
        Assert.Contains("automatique", o.Summary.ToLowerInvariant());
    }

    [Fact]
    public void Revert_StaticStillPresent_NotVerified()
    {
        var o = new DnsApplyOutcome("Ethernet", true, Array.Empty<string>(), new[] { "1.1.1.1" }, DnsApplyStatus.Succeeded);
        Assert.False(o.Verified);
        Assert.Contains("subsiste", o.Summary);
    }

    [Fact]
    public void Failed_NotVerified_SaysSo()
    {
        var o = new DnsApplyOutcome("Ethernet", false, Pair, Array.Empty<string>(), DnsApplyStatus.Failed);
        Assert.False(o.Verified);
        Assert.Contains("échoué", o.Summary);
    }

    [Fact]
    public void NotAttempted_NeverASuccess()
    {
        var o = DnsApplyOutcome.NotAttempted("Ethernet", revert: false);
        Assert.False(o.Verified);
        Assert.Equal(DnsApplyStatus.NotAttempted, o.Status);
        Assert.Contains("Aucun changement", o.Summary);
    }

    [Fact]
    public void RebootRequired_WithMatch_IsVerified()
    {
        var o = new DnsApplyOutcome("Ethernet", false, Pair, Pair, DnsApplyStatus.RebootRequired);
        Assert.True(o.Verified);
    }
}

public class DnsReportTests
{
    [Fact]
    public void Failed_IsHonestAboutUnreadableConfig()
    {
        Assert.False(DnsReport.Failed.QueryOk);
        Assert.False(DnsReport.Failed.Any);
        Assert.Contains("impossible", DnsReport.Failed.Headline);
    }

    [Fact]
    public void Empty_ReportsNoAdapters()
    {
        var r = new DnsReport(Array.Empty<DnsAdapterState>(), true);
        Assert.Equal(0, r.Count);
        Assert.False(r.Any);
        Assert.Contains("Aucune carte", r.Headline);
        Assert.Null(r.Active);
    }

    [Fact]
    public void Mixed_TalliesAndPicksFirstConnectedAsActive()
    {
        var staticConnected = DnsFixtures.Adapter(DnsMode.Static, staticServers: new[] { "1.1.1.1" },
                                                  connected: true, description: "Ethernet");
        var autoOffline = DnsFixtures.Adapter(DnsMode.Automatic, connected: false,
                                              description: "Wi-Fi", settingId: "{GUID-2}");
        var r = new DnsReport(new[] { staticConnected, autoOffline }, true);

        Assert.Equal(2, r.Count);
        Assert.Equal(1, r.StaticCount);
        Assert.Equal(1, r.AutomaticCount);
        Assert.Same(staticConnected, r.Active);
        Assert.Contains("2 carte", r.Headline);
    }

    [Fact]
    public void NoneConnected_ActiveIsNull()
    {
        var r = new DnsReport(new[] { DnsFixtures.Adapter(DnsMode.Automatic, connected: false) }, true);
        Assert.Null(r.Active);
    }
}

public class DnsServiceMappingTests
{
    [Theory]
    [InlineData(0u, DnsApplyStatus.Succeeded)]
    [InlineData(1u, DnsApplyStatus.RebootRequired)]
    [InlineData(2u, DnsApplyStatus.Failed)]
    [InlineData(5u, DnsApplyStatus.Failed)]
    [InlineData(uint.MaxValue, DnsApplyStatus.Failed)]
    public void MapReturn_MapsWmiCodes(uint code, DnsApplyStatus expected) =>
        Assert.Equal(expected, DnsService.MapReturn(code));
}

/// <summary>
/// Pins <see cref="DnsRecommendation.From"/> — the verdict that closes the measure→apply loop. The load-bearing
/// rule: <see cref="DnsRecommendation.CanApply"/> is true ONLY on a genuine, applicable change; every other path
/// (nobody answered, the user's own DNS already won, the winner has no curated preset, no active adapter, the
/// adapter already runs it, or its state is unreadable) yields a false verdict that says exactly why — so the
/// « Appliquer » button can never be a no-op and never claims an improvement that isn't real. Reports are built
/// through the real <see cref="DnsBenchmarkMath.Rank"/> so the winner + median the verdict quotes are honest.
/// </summary>
public class DnsRecommendationTests
{
    private static DnsResolver Res(string name, string address, bool current = false) => new(name, address, current);
    private static DnsProbeResult Probe(DnsResolver r, double medianMs) => DnsBenchmarkMath.Summarize(r, new[] { medianMs }, 1);
    private static DnsProbeResult Silent(DnsResolver r) => DnsBenchmarkMath.Summarize(r, Array.Empty<double>(), 1);
    private static DnsBenchmarkReport Report(params DnsProbeResult[] results) => DnsBenchmarkMath.Rank(results);

    [Fact]
    public void NoResolverAnswered_NoApply_RelaysTheBenchmarkReason()
    {
        var report = Report(Silent(Res("Cloudflare", "1.1.1.1")), Silent(Res("Google", "8.8.8.8")));

        var rec = DnsRecommendation.From(report, DnsFixtures.Adapter(DnsMode.Automatic));

        Assert.False(rec.CanApply);
        Assert.Null(rec.Preset);
        Assert.Contains("Aucun résolveur", rec.Message);   // the benchmark's own honest phrasing, not a fake verdict
    }

    [Fact]
    public void UsersOwnDnsWon_NoApply_EvenWhenItIsACuratedAddress()
    {
        // User is already on Cloudflare (marked current) and it wins — we must not tell them to "apply Cloudflare".
        var report = Report(Probe(Res("Cloudflare", "1.1.1.1", current: true), 5.0),
                            Probe(Res("Google", "8.8.8.8"), 20.0));

        var rec = DnsRecommendation.From(report, DnsFixtures.Adapter(DnsMode.Automatic));

        Assert.False(rec.CanApply);
        Assert.Contains("déjà le plus rapide", rec.Message);
        Assert.Contains("1.1.1.1", rec.Message);
    }

    [Fact]
    public void WinnerHasNoCuratedPreset_NoApply_SaysSo()
    {
        var report = Report(Probe(Res("FAI", "203.0.113.5"), 8.0),
                            Probe(Res("Cloudflare", "1.1.1.1"), 20.0));

        var rec = DnsRecommendation.From(report, DnsFixtures.Adapter(DnsMode.Automatic));

        Assert.False(rec.CanApply);
        Assert.Null(rec.Preset);
        Assert.Contains("sans préréglage", rec.Message);
    }

    [Fact]
    public void NoActiveAdapter_NamesTheWinnerButCannotApply()
    {
        var report = Report(Probe(Res("Cloudflare", "1.1.1.1"), 8.0),
                            Probe(Res("Google", "8.8.8.8"), 20.0));

        var rec = DnsRecommendation.From(report, active: null);

        Assert.False(rec.CanApply);
        Assert.Equal("Cloudflare", rec.Preset!.Name);   // winner known…
        Assert.Contains("aucune carte réseau active", rec.Message);   // …but nowhere to write it
    }

    [Fact]
    public void ActiveAdapterAlreadyRunsTheWinner_NoApply_NoOpDisclosed()
    {
        var cf = DnsFixtures.Cloudflare;
        var report = Report(Probe(Res("Cloudflare", "1.1.1.1"), 8.0));
        var active = DnsFixtures.Adapter(DnsMode.Static, staticServers: cf.Addresses.ToArray());

        var rec = DnsRecommendation.From(report, active);

        Assert.False(rec.CanApply);
        Assert.Contains("utilise déjà", rec.Message);
    }

    [Fact]
    public void ActiveAdapterStateUnreadable_NeverWritesBlind()
    {
        var report = Report(Probe(Res("Cloudflare", "1.1.1.1"), 8.0));
        var active = DnsFixtures.Adapter(DnsMode.Unknown, settingId: "");

        var rec = DnsRecommendation.From(report, active);

        Assert.False(rec.CanApply);
        Assert.Contains("indéterminé", rec.Message);
    }

    [Fact]
    public void ApplicableWinner_CanApply_NamesPresetAdapterAndMeasuredLatency()
    {
        var report = Report(Probe(Res("Cloudflare", "1.1.1.1"), 8.0),
                            Probe(Res("Google", "8.8.8.8"), 30.0));
        var active = DnsFixtures.Adapter(DnsMode.Automatic, description: "Intel Ethernet");

        var rec = DnsRecommendation.From(report, active);

        Assert.True(rec.CanApply);
        Assert.Equal("Cloudflare", rec.Preset!.Name);
        Assert.Same(active, rec.Adapter);
        Assert.Contains("Intel Ethernet", rec.Message);
        Assert.Contains("8 ms", rec.Message);   // the real measured median, never invented
    }

    // The reason FindByPrimary exists, proven end-to-end: a winner the benchmark calls "Google" (which Find() misses)
    // still bridges to the "Google Public DNS" preset by address and becomes a genuine one-click apply.
    [Fact]
    public void WinnerNamedDifferentlyFromCatalog_StillBridgesByAddress_AndApplies()
    {
        var report = Report(Probe(Res("Google", "8.8.8.8"), 9.0),
                            Probe(Res("Cloudflare", "1.1.1.1"), 25.0));
        var active = DnsFixtures.Adapter(DnsMode.Automatic);

        var rec = DnsRecommendation.From(report, active);

        Assert.True(rec.CanApply);
        Assert.Equal("Google Public DNS", rec.Preset!.Name);   // bridged by 8.8.8.8, not by the name "Google"
    }
}
