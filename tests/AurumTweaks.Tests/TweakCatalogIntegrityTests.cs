using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Validates the SHIPPED tweak catalog — the real JSON under <c>/Tweaks</c>, linked into the test
/// output — through the real <see cref="TweakRepository"/> loader. This is the catalog's safety net:
/// it pins the promises the UI makes about every tweak. They load; ids are non-empty and unique;
/// names are bilingual (fr+en); each carries at least one operation; none uses an operation type the
/// engine can't actually run; and — the load-bearing honesty/safety guarantee — every operation of a
/// tweak marked <c>reversible:true</c> has a working revert path that <see cref="TweakService"/> can drive.
///
/// If any of these fail it is a real defect (a dead operation, a broken "réversible" claim, a missing
/// translation), not a flaky test — fix the JSON, don't weaken the assertion.
/// </summary>
public class TweakCatalogIntegrityTests : IClassFixture<TweakCatalogIntegrityTests.CatalogFixture>
{
    private readonly CatalogFixture _fx;
    public TweakCatalogIntegrityTests(CatalogFixture fx) => _fx = fx;

    /// <summary>Loads the real catalog once for the whole class via the production loader.</summary>
    public sealed class CatalogFixture
    {
        public IReadOnlyList<Tweak> Tweaks { get; }
        public CatalogFixture() => Tweaks = new TweakRepository().LoadAllAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public void Catalog_Loads_AndIsNonTrivial()
    {
        // Failure here means either the JSON wasn't copied into the test output (csproj <Content> link)
        // or the loader regressed. The shipped catalog has dozens of tweaks; guard a sane floor.
        Assert.True(_fx.Tweaks.Count >= 50,
            $"Expected the shipped catalog to load (≥50 tweaks); got {_fx.Tweaks.Count}.");
    }

    [Fact]
    public void EveryTweak_HasNonEmptyId_AndIdsAreUnique()
    {
        Assert.All(_fx.Tweaks, t => Assert.False(string.IsNullOrWhiteSpace(t.Id), "a tweak has an empty id"));

        var dupes = _fx.Tweaks
            .GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} (x{g.Count()})")
            .ToList();

        Assert.True(dupes.Count == 0, "duplicate tweak ids: " + string.Join(", ", dupes));
    }

    [Fact]
    public void EveryTweak_HasBilingualName_AndFrenchDescription()
    {
        foreach (var t in _fx.Tweaks)
        {
            Assert.True(t.Name.TryGetValue("fr", out var fr) && !string.IsNullOrWhiteSpace(fr),
                $"tweak '{t.Id}' is missing a non-empty French name");
            Assert.True(t.Name.TryGetValue("en", out var en) && !string.IsNullOrWhiteSpace(en),
                $"tweak '{t.Id}' is missing a non-empty English name");
            Assert.True(t.Description.TryGetValue("fr", out var dfr) && !string.IsNullOrWhiteSpace(dfr),
                $"tweak '{t.Id}' is missing a non-empty French description");
        }
    }

    [Fact]
    public void EveryTweak_HasAtLeastOneOperation()
    {
        foreach (var t in _fx.Tweaks)
            Assert.True(t.Operations.Count > 0, $"tweak '{t.Id}' has no operations");
    }

    [Fact]
    public void NoTweak_UsesAnOperationTypeTheEngineCannotExecute()
    {
        // TweakService.ExecuteAsync has no File branch (it falls through to 'return false'), so a File
        // operation would silently no-op — a dead operation. Guard against shipping one until/unless
        // the engine learns to execute it.
        var offenders = _fx.Tweaks
            .Where(t => t.Operations.Any(o => o.Type == OperationType.File))
            .Select(t => t.Id)
            .ToList();

        Assert.True(offenders.Count == 0,
            "tweaks use the unimplemented File op (a no-op in TweakService): " + string.Join(", ", offenders));
    }

    [Fact]
    public void EveryReversibleTweak_HasAWorkingRevertPath_ForEveryOperation()
    {
        var problems = new List<string>();

        foreach (var t in _fx.Tweaks)
        {
            // A tweak honestly marked reversible:false (e.g. a DNS/Winsock reset) is exempt: it never
            // claims to be undoable, so the UI won't offer a revert for it.
            if (!t.Reversible) continue;

            foreach (var op in t.Operations)
            {
                string? why = op.Type switch
                {
                    // Registry revert writes Revert, or DELETES the value when Revert is null. Either way it
                    // needs a concrete target. (A null Revert == "delete on revert" is a valid, intentional path.)
                    OperationType.Registry
                        when string.IsNullOrWhiteSpace(op.Hive) || string.IsNullOrWhiteSpace(op.Key) || string.IsNullOrWhiteSpace(op.Name)
                        => "registry op missing hive/key/name",

                    // Service revert sets StartupRevert; TweakService refuses an empty target on revert.
                    OperationType.Service
                        when string.IsNullOrWhiteSpace(op.ServiceName) || string.IsNullOrWhiteSpace(op.StartupApply) || string.IsNullOrWhiteSpace(op.StartupRevert)
                        => "service op missing serviceName/startupApply/startupRevert",

                    // Script ops revert by running RevertScript — a reversible tweak must provide one (and an apply Script).
                    (OperationType.PowerShell or OperationType.Cmd or OperationType.Bcdedit)
                        when string.IsNullOrWhiteSpace(op.Script) || string.IsNullOrWhiteSpace(op.RevertScript)
                        => "script op missing script/revertScript",

                    // AppX revert re-registers the package; it needs the package name to do so.
                    OperationType.AppX when string.IsNullOrWhiteSpace(op.AppxPackage)
                        => "appx op missing appxPackage",

                    // ScheduledTask revert re-enables the task; it needs the task path.
                    OperationType.ScheduledTask when string.IsNullOrWhiteSpace(op.TaskPath)
                        => "scheduledTask op missing taskPath",

                    // File ops cannot be executed (or reverted) by the engine at all.
                    OperationType.File => "File op is not executable by the engine",

                    _ => null
                };

                if (why is not null)
                    problems.Add($"{t.Id}: {why}");
            }
        }

        Assert.True(problems.Count == 0,
            "reversible tweaks with a broken revert path:\n  " + string.Join("\n  ", problems));
    }

    [Fact]
    public void TranquilleTier_IsAGenuineSafetyPromise_LowRiskAndAntiCheatClean()
    {
        // The "Tranquille" tier badge tells every user "safe for you, no worries" — it's the tier shown to
        // non-experts and the backbone of the safe one-click recommended set. That promise has two halves,
        // and a future catalog edit must not quietly betray either by slipping a spicy tweak under the calm
        // label:
        //   • it must not endanger the system  → risk stays None/Low (never Medium+/HardwareDamage);
        //   • it must not endanger the account → no anti-cheat concern (every AC status Safe).
        // Both hold in the shipped catalog today; this pins them so a regression is caught at the source.
        var tooRisky = _fx.Tweaks
            .Where(t => t.Tier == TweakTier.Tranquille && t.Risk >= RiskLevel.Medium)
            .Select(t => $"{t.Id} (risk={t.Risk})")
            .ToList();
        Assert.True(tooRisky.Count == 0,
            "Tranquille tweaks must stay low-risk, but these are Medium+: " + string.Join(", ", tooRisky));

        var banRisk = _fx.Tweaks
            .Where(t => t.Tier == TweakTier.Tranquille && t.AntiCheat.HasAnyConcern)
            .Select(t => t.Id)
            .ToList();
        Assert.True(banRisk.Count == 0,
            "Tranquille tweaks must carry no anti-cheat concern, but these do: " + string.Join(", ", banRisk));
    }

    [Theory]
    // A tweak whose name/description promises a hardware constraint must ENCODE that constraint in its
    // `applicability` gate — otherwise the adaptive "recommended for your PC" engine
    // (AdaptiveRecommendationService.IsApplicable) surfaces it on the very machines its own copy warns
    // against: e.g. recommending "Désactiver Superfetch/SysMain (SSD only)" on an HDD, or a desktop-only
    // C-state / core-parking tweak on a laptop. Structural integrity (revert path, bilingual copy) cannot
    // see this — it is a semantic promise. Regression pin: disable-superfetch-ssd shipped with the SSD-only
    // warning but no ssdOnly gate; this guards that class of mismatch across the hardware-specific tweaks.
    [InlineData("disable-superfetch-ssd", "ssd")]
    [InlineData("disable-core-parking", "desktop")]
    [InlineData("disable-cpu-idle-desktop", "desktop")]
    [InlineData("ntfs-memory-usage-boost", "ram16")]
    [InlineData("disable-memory-compression", "ram32")]
    [InlineData("nvidia-disable-telemetry", "nvidia")]
    [InlineData("nvidia-prefer-max-performance", "nvidia")]
    [InlineData("amd-disable-ulps", "amd")]
    public void HardwareConstrainedTweak_EncodesTheConstraintItsCopyPromises(string id, string axis)
    {
        var t = _fx.Tweaks.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        Assert.True(t is not null, $"tweak '{id}' not found in the shipped catalog");

        var a = t!.Applicability;
        Assert.True(a is not null, $"tweak '{id}' promises a hardware constraint in its copy but has no applicability block");

        bool gated = axis switch
        {
            "ssd"     => a!.SsdOnly,
            "desktop" => a!.DesktopOnly,
            "ram16"   => a!.MinRamGb >= 16,
            "ram32"   => a!.MinRamGb >= 32,
            "nvidia"  => a!.GpuVendors.Contains(GpuVendor.Nvidia),
            "amd"     => a!.GpuVendors.Contains(GpuVendor.Amd),
            _         => false
        };
        Assert.True(gated, $"tweak '{id}' must encode the '{axis}' constraint its name/description promises " +
            "(so the adaptive engine hides it on machines its own warning says not to apply it to)");
    }

    [Fact]
    public void NoExpectedImpact_PromisesASpecificMillisecondLatencyDelta()
    {
        // Honesty mandate (no fabricated metrics): the app never measures a before/after latency delta,
        // so a precise "-X ms" figure in expectedImpact is invented precision — the same defect class struck
        // when "Latence -10ms", "Latence -2-5 ms", "Latence d'entrée -5 à -15 ms", "Résolution DNS ~10 ms"
        // were softened to honest qualitative direction. A latency *direction* ("Latence réduite") is an
        // honest design claim; a millisecond *number* reads as a measurement the app cannot make. This pins
        // the sharpest, zero-false-positive instance of the broader rule (a digit adjacent to "ms", so words
        // like "frametimes"/"timers" never trip it); the %/W/°C deltas were cleaned in the same pass but can't
        // be pinned as crisply because "%" appears legitimately (e.g. "100% disponible", "1% low").
        var msDelta = new Regex(@"\d\s*ms\b", RegexOptions.IgnoreCase);

        var offenders = _fx.Tweaks
            .Where(t => msDelta.IsMatch(t.ExpectedImpact))
            .Select(t => $"{t.Id}: \"{t.ExpectedImpact}\"")
            .ToList();

        Assert.True(offenders.Count == 0,
            "expectedImpact must not promise a specific millisecond latency delta (the app never measures it):\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void NoReversibleTweak_HasARegistryOpThatRevertsToItsOwnAppliedValue()
    {
        // A reversible tweak whose registry op writes the SAME value on revert as on apply cannot undo that
        // write: when the value differs from the Windows default it's a silent broken-revert — a fake
        // "réversible" promise. EveryReversibleTweak_HasAWorkingRevertPath only checks the revert TARGET
        // exists, not that it restores a DIFFERENT state, so this closes that gap. (Revert==null means
        // "delete on revert" — a valid, genuinely-different path — so it's exempt.) Two shipped ops
        // legitimately write apply==revert because the value IS the Windows default and the tweak's real
        // state-change lives in SIBLING ops; they're allow-listed with the reason. Any NEW apply==revert pair
        // must be justified here or fixed.
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // MMCSS "Games" GPU Priority default IS 8; the real tweak is its siblings (Priority 2->6,
            // Scheduling Category Medium->High). Writing 8 on revert restores that default — not a no-undo.
            "games-task-priority::GPU Priority",
            // The mask must stay 3 in BOTH states for the real toggle (FeatureSettingsOverride 3=off / 0=on)
            // to take effect; reverting the mask to 0/absent would itself break the mitigation re-enable.
            "disable-spectre-meltdown-mitigations::FeatureSettingsOverrideMask",
        };

        var offenders = _fx.Tweaks
            .Where(t => t.Reversible)
            .SelectMany(t => t.Operations.Select(op => (t, op)))
            .Where(x => x.op.Type == OperationType.Registry
                        && x.op.Apply is not null && x.op.Revert is not null
                        && string.Equals(x.op.Apply, x.op.Revert, StringComparison.Ordinal)
                        && !allowed.Contains($"{x.t.Id}::{x.op.Name}"))
            .Select(x => $"{x.t.Id}::{x.op.Name} (apply==revert=={x.op.Apply})")
            .ToList();

        Assert.True(offenders.Count == 0,
            "reversible tweaks have a registry op that 'reverts' to its own applied value (broken revert "
            + "unless the value is the OS default):\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void EveryServiceOp_UsesACanonicalStartupType_SoOnSystemDetectionRoundTrips()
    {
        // On-system detection compares a service's live Start value — read back as a canonical string by
        // ServiceManagerService.TryGetStartupType — against the catalog's startupApply. The apply path
        // (SetStartupType) is lenient: it accepts "auto" and silently maps any unrecognized string to Manual.
        // So a non-canonical value would APPLY yet never DETECT as applied: the "✓ Appliqué" badge stays dark
        // on a service that's genuinely live (false negative), or a typo'd target applies as Manual with no
        // error (false success). Pin every service startup value to the round-trippable vocabulary so neither
        // mismatch can ship. startupApply is always required; startupRevert only when present (a non-reversible
        // service op needs no revert target).
        var offenders = new List<string>();
        foreach (var t in _fx.Tweaks)
            foreach (var op in t.Operations.Where(o => o.Type == OperationType.Service))
            {
                if (!ServiceStartup.IsCanonical(op.StartupApply))
                    offenders.Add($"{t.Id}::{op.ServiceName} startupApply='{op.StartupApply}'");
                if (!string.IsNullOrEmpty(op.StartupRevert) && !ServiceStartup.IsCanonical(op.StartupRevert))
                    offenders.Add($"{t.Id}::{op.ServiceName} startupRevert='{op.StartupRevert}'");
            }

        Assert.True(offenders.Count == 0,
            "service ops must use a canonical startup type so on-system detection round-trips (not e.g. \"auto\", "
            + "which applies but reads back \"Automatic\"):\n  " + string.Join("\n  ", offenders)
            + "\n  canonical: " + string.Join(", ", ServiceStartup.Canonical));
    }
}
