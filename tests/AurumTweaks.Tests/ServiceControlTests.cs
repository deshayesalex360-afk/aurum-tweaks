using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the curated <see cref="ServiceCatalog"/> behind the « Services Windows » page. The load-bearing honesty
/// invariants: the list is a hand-picked allow-list that NEVER contains a system-critical service (RPC, DCOM, the
/// filtering engine, Defender, the networking stack, audio, crypto, profile, Event Log, the task scheduler…) —
/// because disabling those bricks or cripples Windows; every category carries a non-empty French label + advice;
/// the recommended target of any non-Keep service is a round-trippable canonical start type; and the gaming/perf
/// buckets are flagged « à conserver » (Keep) so the page never pushes a gamer to break Game Pass or controllers.
/// </summary>
public class ServiceCatalogTests
{
    public static IEnumerable<object[]> AllCategories =>
        Enum.GetValues<ServiceCategory>().Select(c => new object[] { c });

    [Fact]
    public void Services_EveryEntry_HasServiceNameAndLabel()
    {
        Assert.NotEmpty(ServiceCatalog.Services);
        foreach (var s in ServiceCatalog.Services)
        {
            Assert.False(string.IsNullOrWhiteSpace(s.ServiceName));
            Assert.False(string.IsNullOrWhiteSpace(s.Label));
            Assert.DoesNotContain(' ', s.ServiceName);   // a real short service name, never a display name
        }
    }

    [Fact]
    public void Services_ServiceNames_AreDistinct()
    {
        var set = new HashSet<string>(ServiceCatalog.Services.Select(s => s.ServiceName), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(ServiceCatalog.Services.Count, set.Count);
    }

    [Theory]
    [MemberData(nameof(AllCategories))]
    public void CategoryLabel_And_Advice_AreNonEmpty_ForEveryCategory(ServiceCategory category)
    {
        Assert.False(string.IsNullOrWhiteSpace(ServiceCatalog.CategoryLabel(category)));
        Assert.False(string.IsNullOrWhiteSpace(ServiceCatalog.Advice(category)));
    }

    [Theory]
    [MemberData(nameof(AllCategories))]
    public void RecommendedAction_IsADefinedRecommendation_ForEveryCategory(ServiceCategory category)
        => Assert.True(Enum.IsDefined(ServiceCatalog.RecommendedAction(category)));

    [Fact]
    public void RecommendedTarget_OfNonKeepServices_IsCanonical_KeepIsEmpty()
    {
        foreach (var s in ServiceCatalog.Services)
        {
            var rec = ServiceCatalog.RecommendedAction(s.Category);
            var target = ServiceCatalog.RecommendedTarget(rec);
            if (rec == ServiceRecommendation.Keep)
                Assert.Equal("", target);                       // nothing to set for « à conserver »
            else
                Assert.True(ServiceStartup.IsCanonical(target),  // round-trips, so detection never reads a false "not applied"
                    $"{s.ServiceName} recommends a non-canonical target '{target}'");
        }
    }

    [Fact]
    public void RecommendedTarget_MapsEachRecommendation()
    {
        Assert.Equal("Disabled", ServiceCatalog.RecommendedTarget(ServiceRecommendation.Disable));
        Assert.Equal("Manual", ServiceCatalog.RecommendedTarget(ServiceRecommendation.Manual));
        Assert.Equal("", ServiceCatalog.RecommendedTarget(ServiceRecommendation.Keep));
    }

    [Fact]
    public void Catalog_XboxServices_AreFlaggedKeep()
    {
        // The gamer counterweight: every Xbox service is « à conserver », and at least one is present.
        var xbox = ServiceCatalog.Services.Where(s => s.Category == ServiceCategory.Xbox).ToList();
        Assert.NotEmpty(xbox);
        Assert.All(xbox, s => Assert.Equal(ServiceRecommendation.Keep, ServiceCatalog.RecommendedAction(s.Category)));
    }

    [Fact]
    public void Catalog_PerformanceServices_AreFlaggedKeep()
    {
        var perf = ServiceCatalog.Services.Where(s => s.Category == ServiceCategory.Performance).ToList();
        Assert.NotEmpty(perf);
        Assert.All(perf, s => Assert.Equal(ServiceRecommendation.Keep, ServiceCatalog.RecommendedAction(s.Category)));
    }

    [Fact]
    public void Catalog_TelemetryServices_AreFlaggedDisable()
    {
        var tele = ServiceCatalog.Services.Where(s => s.Category == ServiceCategory.Telemetry).ToList();
        Assert.NotEmpty(tele);
        Assert.All(tele, s => Assert.Equal(ServiceRecommendation.Disable, ServiceCatalog.RecommendedAction(s.Category)));
    }

    [Fact]
    public void Catalog_OffersEachRecommendation_SoThePageIsActionableAndHasACounterweight()
    {
        var recs = ServiceCatalog.Services.Select(s => ServiceCatalog.RecommendedAction(s.Category)).ToHashSet();
        Assert.Contains(ServiceRecommendation.Disable, recs);   // genuinely actionable…
        Assert.Contains(ServiceRecommendation.Manual, recs);    // …with the light-touch option…
        Assert.Contains(ServiceRecommendation.Keep, recs);      // …and the honesty « à conserver » counterweight.
    }

    // Services whose disabling/removal bricks or severely cripples Windows. The catalog must never list any of them.
    // Exact short names (not fragments): service names are stable + exact, and the catalog deliberately includes
    // legitimate "Remote*" entries that a substring denylist would wrongly flag.
    private static readonly string[] CriticalServices =
    {
        "RpcSs", "RpcEptMapper", "DcomLaunch", "BrokerInfrastructure", "SystemEventsBroker", "CoreMessagingRegistrar",
        "BFE", "mpssvc", "WinDefend", "WdNisSvc", "SecurityHealthService", "wscsvc",
        "Dhcp", "Dnscache", "nsi", "NlaSvc", "netprofm", "Netman", "WlanSvc",
        "ProfSvc", "UserManager", "Audiosrv", "AudioEndpointBuilder", "CryptSvc", "gpsvc", "SamSs",
        "EventLog", "EventSystem", "Schedule", "LanmanServer", "LanmanWorkstation", "Winmgmt",
        "Power", "PlugPlay", "Themes", "ShellHWDetection", "Wcmsvc", "TrustedInstaller", "StateRepository", "LSM",
    };

    [Fact]
    public void Catalog_ExcludesCriticalServices()
    {
        foreach (var s in ServiceCatalog.Services)
            Assert.DoesNotContain(CriticalServices, c => c.Equals(s.ServiceName, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Pins the pure French startup-type display so it never leans on Windows' localized service text.</summary>
public class ServiceStartupDisplayTests
{
    [Theory]
    [InlineData("Boot", "Démarrage noyau (boot)")]
    [InlineData("System", "Système")]
    [InlineData("Automatic", "Automatique")]
    [InlineData("DelayedAuto", "Automatique (différé)")]
    [InlineData("Manual", "Manuel (déclenché)")]
    [InlineData("Disabled", "Désactivé")]
    [InlineData("Unknown", "Inconnu")]
    [InlineData(null, "Inconnu")]
    public void Label_MapsTheCanonicalVocabulary(string? startup, string expected)
        => Assert.Equal(expected, ServiceStartupDisplay.Label(startup));

    [Fact]
    public void Label_CoversEveryCanonicalValue()
    {
        // No canonical start type should fall through to "Inconnu" — that would mislabel a live service.
        foreach (var canonical in ServiceStartup.Canonical)
            Assert.NotEqual("Inconnu", ServiceStartupDisplay.Label(canonical));
    }
}

/// <summary>
/// Pins <see cref="ServiceResolver.Resolve"/> and the <see cref="ServiceEntry"/> display/gate logic — the pure join
/// of the curated catalog with the live "name → state" map, its actionable-first ordering, and the load-bearing
/// honesty gates: an absent service is never invented (no buttons), a « à conserver » service is never nudged toward
/// disabling, a driver-like Boot/System start is shown read-only, and a startup button is offered only when it would
/// actually change something (no dead control).
/// </summary>
public class ServiceResolverTests
{
    private static readonly ManagedServiceInfo Tele = new("FakeTele", "Tele", ServiceCategory.Telemetry);     // → Disable
    private static readonly ManagedServiceInfo Perf = new("FakePerf", "Perf", ServiceCategory.Performance);   // → Keep
    private static readonly ManagedServiceInfo OnDem = new("FakeOnDem", "OnDem", ServiceCategory.OnDemand);    // → Manual
    private static readonly ManagedServiceInfo Gone = new("FakeGone", "Gone", ServiceCategory.RemoteAccess);  // → Disable, absent

    private static readonly IReadOnlyList<ManagedServiceInfo> Catalog = new[] { Tele, Perf, OnDem, Gone };

    private static IReadOnlyDictionary<string, ServiceLiveState> Map(params ServiceLiveState[] states)
        => states.ToDictionary(s => s.ServiceName, s => s, StringComparer.OrdinalIgnoreCase);

    private static ServiceLiveState Live(ManagedServiceInfo info, string startup, bool running = false)
        => new(info.ServiceName, startup, Exists: true, IsRunning: running);

    private static ServiceEntry Entry(ManagedServiceInfo info, IReadOnlyDictionary<string, ServiceLiveState> map)
        => ServiceResolver.Resolve(Catalog, map).Single(e => e.Info == info);

    [Fact]
    public void Resolve_NameInMap_UsesLiveState()
    {
        var e = Entry(Tele, Map(Live(Tele, "Automatic", running: true)));

        Assert.True(e.IsPresent);
        Assert.True(e.IsRunning);
        Assert.Equal("Automatic", e.StartupType);
        Assert.Equal("Automatique", e.StartupDisplay);
        Assert.Equal("En cours d'exécution", e.StateDisplay);
    }

    [Fact]
    public void Resolve_NameNotInMap_IsAbsent_NotInvented()
    {
        var e = Entry(Gone, Map());

        Assert.False(e.IsPresent);
        Assert.Null(e.StartupType);
        Assert.False(e.ShowActions);          // an absent service offers no buttons
        Assert.False(e.IsTunable);
        Assert.True(e.ShowAbsentBadge);
        Assert.Equal("Absent sur ce PC", e.StateDisplay);
    }

    [Fact]
    public void Resolve_EmptyMap_EverythingAbsent()
        => Assert.All(ServiceResolver.Resolve(Catalog, Map()), e => Assert.False(e.IsPresent));

    [Fact]
    public void Entry_DisableService_StillAutomatic_IsActionable()
    {
        var e = Entry(Tele, Map(Live(Tele, "Automatic")));

        Assert.True(e.ShowActions);
        Assert.False(e.IsAtRecommended);
        Assert.False(e.ShowOptimizedBadge);
        Assert.Equal("Recommandé : Désactivé", e.RecommendChip);
        Assert.False(e.CanSetAuto);           // already Automatic — that button would be a no-op
        Assert.True(e.CanSetManual);
        Assert.True(e.CanSetDisabled);
    }

    [Fact]
    public void Entry_DisableService_AlreadyDisabled_IsOptimised()
    {
        var e = Entry(Tele, Map(Live(Tele, "Disabled")));

        Assert.True(e.IsAtRecommended);
        Assert.True(e.ShowOptimizedBadge);
        Assert.False(e.CanSetDisabled);       // already there
        Assert.True(e.CanSetAuto);
        Assert.True(e.CanSetManual);
    }

    [Fact]
    public void Entry_ManualService_AtManual_IsOptimised()
    {
        var e = Entry(OnDem, Map(Live(OnDem, "Manual")));

        Assert.Equal("Recommandé : Manuel", e.RecommendChip);
        Assert.True(e.IsAtRecommended);
        Assert.True(e.ShowOptimizedBadge);
        Assert.False(e.CanSetManual);
    }

    [Fact]
    public void Entry_KeepService_ShowsKeepBadge_AndOffersNoActions()
    {
        var e = Entry(Perf, Map(Live(Perf, "Automatic", running: true)));

        Assert.Equal(ServiceRecommendation.Keep, e.Recommendation);
        Assert.False(e.ShowActions);          // never nudge a « à conserver » service toward disabling
        Assert.True(e.ShowKeepBadge);
        Assert.False(e.IsAtRecommended);      // Keep is never "optimised away"
        Assert.False(e.ShowOptimizedBadge);
        Assert.False(e.CanSetDisabled);
    }

    [Theory]
    [InlineData("Boot")]
    [InlineData("System")]
    [InlineData("Unknown")]
    public void Entry_DriverLikeOrUnreadableStart_IsNotTunable(string startup)
    {
        // A Boot/System (driver-like) or Unknown start type is shown read-only — we never offer to flip its mode.
        var e = Entry(Tele, Map(Live(Tele, startup)));

        Assert.True(e.IsPresent);
        Assert.False(e.IsTunable);
        Assert.False(e.ShowActions);
        Assert.False(e.CanSetAuto);
        Assert.False(e.CanSetManual);
        Assert.False(e.CanSetDisabled);
    }

    [Fact]
    public void Entry_DelayedAuto_IsTunable_AndDistinctFromAutomatic()
    {
        var e = Entry(Tele, Map(Live(Tele, "DelayedAuto")));

        Assert.True(e.IsTunable);
        Assert.True(e.CanSetAuto);            // DelayedAuto → Automatic is a real change
        Assert.True(e.CanSetManual);
        Assert.True(e.CanSetDisabled);
    }

    [Fact]
    public void Resolve_Orders_Actionable_ThenKeep_ThenOptimised_ThenAbsent()
    {
        // Tele Automatic (actionable), Perf Automatic (keep), OnDem Manual (optimised), Gone absent.
        var entries = ServiceResolver.Resolve(Catalog, Map(
            Live(Tele, "Automatic"),
            Live(Perf, "Automatic"),
            Live(OnDem, "Manual")));

        Assert.Equal(new[] { "Tele", "Perf", "OnDem", "Gone" }, entries.Select(e => e.Label).ToArray());
    }

    [Fact]
    public void Report_ActionableCount_CountsOnlyNotYetAtRecommended()
    {
        var report = new ServiceControlReport(
            ServiceResolver.Resolve(Catalog, Map(
                Live(Tele, "Automatic"),   // actionable (Disable, not yet disabled)
                Live(Perf, "Automatic"),   // keep — never actionable
                Live(OnDem, "Manual"))),   // already at recommended — not actionable
            QueryOk: true);

        Assert.Equal(1, report.ActionableCount);
        Assert.Equal(3, report.PresentCount);
    }
}

/// <summary>
/// Pins the thin I/O glue in <see cref="ServiceControlService"/> against a <see cref="FakeServiceManager"/> — no real
/// services are touched. The load-bearing decisions: « Désactivé » also STOPS the running service (so the change is
/// true now, not merely next boot) while « Manuel »/« Automatique » leave it running; a non-canonical or empty target
/// is refused with NO write (it would otherwise make on-system detection read a false state).
/// </summary>
public class ServiceControlServiceTests
{
    private static (ServiceControlService Svc, FakeServiceManager Mgr, EventLog Log) Build()
    {
        var log = new EventLog();
        var mgr = new FakeServiceManager(log);
        return (new ServiceControlService(mgr), mgr, log);
    }

    [Fact]
    public async Task SetStartup_Disabled_WritesThenStops()
    {
        var (svc, mgr, log) = Build();

        var ok = await svc.SetStartupAsync("DiagTrack", "Disabled");

        Assert.True(ok);
        Assert.Equal("Disabled", mgr.Startup["DiagTrack"]);
        Assert.Equal(new[] { "svc.startup:DiagTrack=Disabled", "svc.stop:DiagTrack" }, log.Events);
    }

    [Fact]
    public async Task SetStartup_Manual_WritesButDoesNotStop()
    {
        var (svc, mgr, log) = Build();

        await svc.SetStartupAsync("MapsBroker", "Manual");

        Assert.Equal("Manual", mgr.Startup["MapsBroker"]);
        Assert.DoesNotContain("svc.stop:MapsBroker", log.Events);
    }

    [Fact]
    public async Task SetStartup_Automatic_WritesButDoesNotStop()
    {
        var (svc, mgr, log) = Build();

        await svc.SetStartupAsync("Spooler", "Automatic");

        Assert.Equal("Automatic", mgr.Startup["Spooler"]);
        Assert.DoesNotContain("svc.stop:Spooler", log.Events);
    }

    [Theory]
    [InlineData("auto")]       // a lenient alias that does NOT round-trip → refused
    [InlineData("nonsense")]
    [InlineData("")]
    public async Task SetStartup_NonCanonicalTarget_IsRefused_WithNoWrite(string target)
    {
        var (svc, _, log) = Build();

        var ok = await svc.SetStartupAsync("DiagTrack", target);

        Assert.False(ok);
        Assert.Empty(log.Events);   // nothing written or stopped
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SetStartup_BlankServiceName_IsRefused_WithNoWrite(string name)
    {
        var (svc, _, log) = Build();

        var ok = await svc.SetStartupAsync(name, "Disabled");

        Assert.False(ok);
        Assert.Empty(log.Events);
    }
}
