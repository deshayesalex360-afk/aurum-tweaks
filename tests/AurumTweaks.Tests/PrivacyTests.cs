using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

public class PrivacyCatalogTests
{
    [Fact]
    public void Settings_NotEmpty() => Assert.NotEmpty(PrivacyCatalog.Settings);

    [Fact]
    public void Ids_AreUnique()
        => Assert.Equal(PrivacyCatalog.Settings.Count,
                        PrivacyCatalog.Settings.Select(s => s.Id).Distinct().Count());

    [Fact]
    public void ValueNames_AreNonEmptyAndSpaceFree()
        => Assert.All(PrivacyCatalog.Settings, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.ValueName));
            Assert.DoesNotContain(' ', s.ValueName);
        });

    /// <summary>
    /// The load-bearing safety guard: this page may ONLY write per-user (HKCU) values or HKLM group-policy keys
    /// (SOFTWARE\Policies\…). It can never reach into an arbitrary HKLM/HKCR location — so a future catalog edit
    /// that strays outside the known consent locations fails the build instead of silently shipping a registry
    /// footgun. Every value is a DWord (the consent switches are all numeric flags).
    /// </summary>
    [Fact]
    public void AllSettings_TargetHkcuOrHklmPolicies()
        => Assert.All(PrivacyCatalog.Settings, s =>
        {
            Assert.Contains(s.Hive, new[] { "HKCU", "HKLM" });
            if (s.Hive == "HKLM")
                Assert.StartsWith(@"SOFTWARE\Policies\", s.Key, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(RegistryValueType.DWord, s.Kind);
        });

    [Fact]
    public void EverySetting_HasDistinctHardenedAndDefaultValues()
        => Assert.All(PrivacyCatalog.Settings, s => Assert.NotEqual(s.HardenedValue, s.DefaultValue));

    [Fact]
    public void EverySetting_HasLabelAndAdvice()
        => Assert.All(PrivacyCatalog.Settings, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Label));
            Assert.False(string.IsNullOrWhiteSpace(s.Advice));
        });

    [Fact]
    public void EveryCategory_HasNonEmptyLabel()
        => Assert.All(Enum.GetValues<PrivacyCategory>(),
                      c => Assert.False(string.IsNullOrWhiteSpace(PrivacyCatalog.CategoryLabel(c))));

    [Fact]
    public void Find_IsCaseInsensitive_AndMatchesById()
    {
        var lower = PrivacyCatalog.Find("advertising-id");
        var upper = PrivacyCatalog.Find("ADVERTISING-ID");
        Assert.NotNull(lower);
        Assert.Same(lower, upper);
    }

    [Theory]
    [InlineData("does-not-exist")]
    [InlineData(null)]
    [InlineData("")]
    public void Find_UnknownOrNull_ReturnsNull(string? id) => Assert.Null(PrivacyCatalog.Find(id));

    /// <summary>
    /// Honesty pin: the telemetry setting must disclose the consumer-SKU floor (0 → plafonné to 1 on Famille/Pro)
    /// rather than implying it fully disables collection. Hardened=0 (Security), default=3 (Full).
    /// </summary>
    [Fact]
    public void Telemetry_DisclosesConsumerFloor()
    {
        var t = PrivacyCatalog.Find("allow-telemetry");
        Assert.NotNull(t);
        Assert.Equal(PrivacyCategory.Telemetry, t!.Category);
        Assert.Equal("HKLM", t.Hive);
        Assert.Equal("0", t.HardenedValue);
        Assert.Equal("3", t.DefaultValue);
        Assert.False(string.IsNullOrWhiteSpace(t.Note));
        Assert.Contains("plafonné", t.Note!);
    }

    [Fact]
    public void AiPolicies_DiscloseLimits_AndRestoreByDeletingPolicyValues()
    {
        var ids = new[] { "recall-snapshots-device", "recall-snapshots-user", "copilot-windows-policy", "click-to-do-policy" };

        foreach (var id in ids)
        {
            var s = PrivacyCatalog.Find(id);
            Assert.NotNull(s);
            Assert.Equal(PrivacyCategory.Ai, s!.Category);
            Assert.True(s.RestoreDeletesValue);
            Assert.Equal("Désactivé par politique", s.HardenedStateDisplay);
            Assert.Contains("mise à jour de fonctionnalité", s.Note!);
        }

        Assert.Contains("pas les données déjà effacées", PrivacyCatalog.Find("recall-snapshots-device")!.Note!);
        Assert.Contains("ne couvre pas tous les produits Copilot", PrivacyCatalog.Find("copilot-windows-policy")!.Note!);
    }
}

public class PrivacySettingStateTests
{
    private static readonly PrivacySetting Dword =
        new("x", "L", "A", PrivacyCategory.Telemetry, "HKCU", "K", "V", RegistryValueType.DWord, "0", "1");
    private static readonly PrivacySetting WithNote =
        new("xn", "L", "A", PrivacyCategory.Telemetry, "HKCU", "K", "V", RegistryValueType.DWord, "0", "1", "caveat note");
    private static readonly PrivacySetting DeleteRestore =
        new("ai", "IA", "A", PrivacyCategory.Ai, "HKLM", PrivacyCatalog.WindowsAiPolicy, "DisableThing",
            RegistryValueType.DWord, "1", "0", "caveat", "Désactivé par politique",
            PrivacyCatalog.AiPolicyDefaultDisplay, RestoreDeletesValue: true);

    [Fact]
    public void Absent_ReadsAsDefault_NotFabricatedHardened()
    {
        var s = new PrivacySettingState(Dword, null, false);
        Assert.True(s.IsDefault);          // absent ⇒ Windows default = collection active
        Assert.False(s.IsHardened);
        Assert.False(s.IsCustomValue);
        Assert.True(s.CanHarden);
        Assert.False(s.CanRestore);        // restoring an absent key is a no-op → no dead button
        Assert.False(s.ShowHardenedBadge);
        Assert.Contains("Non configuré", s.StateDisplay);
    }

    [Fact]
    public void PresentHardened_CanOnlyRestore_AndBadges()
    {
        var s = new PrivacySettingState(Dword, "0", true);
        Assert.True(s.IsHardened);
        Assert.False(s.IsDefault);
        Assert.False(s.CanHarden);
        Assert.True(s.CanRestore);
        Assert.True(s.ShowHardenedBadge);
        Assert.Equal("Protégé (collecte réduite)", s.StateDisplay);
    }

    [Fact]
    public void PresentDefault_CanOnlyHarden()
    {
        var s = new PrivacySettingState(Dword, "1", true);
        Assert.True(s.IsDefault);
        Assert.False(s.IsHardened);
        Assert.True(s.CanHarden);
        Assert.False(s.CanRestore);
        Assert.Equal("Défaut Windows (collecte active)", s.StateDisplay);
    }

    [Fact]
    public void PresentCustomValue_IsNeither_BothActionsOffered()
    {
        var s = new PrivacySettingState(Dword, "5", true);
        Assert.True(s.IsCustomValue);
        Assert.False(s.IsHardened);
        Assert.False(s.IsDefault);
        Assert.True(s.CanHarden);
        Assert.True(s.CanRestore);
        Assert.Contains("5", s.StateDisplay);
    }

    [Fact]
    public void RestoreDeletesValue_TreatsOnlyAbsenceAsDefault()
    {
        var absent = new PrivacySettingState(DeleteRestore, null, false);
        Assert.True(absent.IsDefault);
        Assert.Equal("Non configuré · choix Windows/utilisateur non forcé", absent.StateDisplay);

        var explicitZero = new PrivacySettingState(DeleteRestore, "0", true);
        Assert.True(explicitZero.IsCustomValue);
        Assert.True(explicitZero.CanRestore); // restore deletes the policy value, not a no-op write of 0

        var hardened = new PrivacySettingState(DeleteRestore, "1", true);
        Assert.True(hardened.IsHardened);
        Assert.Equal("Désactivé par politique", hardened.StateDisplay);
    }

    [Fact]
    public void DefaultDisplay_IsSourcedFromSetting()
    {
        var setting = Dword with { DefaultStateDisplay = "Défaut unique" };
        Assert.Equal("Non configuré · Défaut unique", new PrivacySettingState(setting, null, false).StateDisplay);
        Assert.Equal("Défaut unique", new PrivacySettingState(setting, "1", true).StateDisplay);
    }

    [Theory]
    [InlineData("0x0", true, false)]   // hex zero matches hardened "0" numerically
    [InlineData("0x1", false, true)]   // hex one matches default "1" numerically
    public void Dword_ComparesNumerically(string live, bool hardened, bool isDefault)
    {
        var s = new PrivacySettingState(Dword, live, true);
        Assert.Equal(hardened, s.IsHardened);
        Assert.Equal(isDefault, s.IsDefault);
    }

    [Fact]
    public void HasNote_FollowsSetting()
    {
        Assert.False(new PrivacySettingState(Dword, null, false).HasNote);
        var n = new PrivacySettingState(WithNote, null, false);
        Assert.True(n.HasNote);
        Assert.Equal("caveat note", n.Note);
    }

    [Fact]
    public void CategoryLabel_SurfacesCatalogLabel()
        => Assert.Equal(PrivacyCatalog.CategoryLabel(PrivacyCategory.Telemetry),
                        new PrivacySettingState(Dword, null, false).CategoryLabel);

    /// <summary>No row may be fully dead: every state offers at least one of Renforcer/Rétablir.</summary>
    [Theory]
    [InlineData("0", true)]    // hardened → restore
    [InlineData("1", true)]    // default  → harden
    [InlineData(null, false)]  // absent   → harden
    [InlineData("5", true)]    // custom   → both
    public void EveryState_OffersAtLeastOneAction(string? live, bool present)
    {
        var s = new PrivacySettingState(Dword, live, present);
        Assert.True(s.CanHarden || s.CanRestore);
    }
}

public class PrivacyPlanTests
{
    [Fact]
    public void HardenAll_CoversEverySettingOnce_WithHardenedValue()
    {
        var plan = PrivacyPlan.HardenAll(PrivacyCatalog.Settings);
        Assert.Equal(PrivacyCatalog.Settings.Count, plan.Count);
        Assert.All(plan, w => Assert.Equal(w.Setting.HardenedValue, w.Value));
    }

    [Fact]
    public void RestoreAll_CoversEverySettingOnce_WithDefaultValue()
    {
        var plan = PrivacyPlan.RestoreAll(PrivacyCatalog.Settings);
        Assert.Equal(PrivacyCatalog.Settings.Count, plan.Count);
        Assert.All(plan, w =>
        {
            if (w.Setting.RestoreDeletesValue)
            {
                Assert.True(w.DeletesValue);
                Assert.Null(w.Value);
            }
            else
            {
                Assert.False(w.DeletesValue);
                Assert.Equal(w.Setting.DefaultValue, w.Value);
            }
        });
    }
}

public class PrivacyFirewallPlanTests
{
    [Fact]
    public void Rules_AreNamedUniqueAndProgramScoped()
    {
        Assert.Equal(PrivacyFirewallPlan.TelemetryRules.Count,
                     PrivacyFirewallPlan.TelemetryRules.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(PrivacyFirewallPlan.TelemetryRules, r =>
        {
            Assert.StartsWith("AurumTweaks.Privacy.Telemetry.", r.Name, StringComparison.Ordinal);
            Assert.StartsWith(@"%SystemRoot%\System32\", r.ProgramPath, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".exe", r.ProgramPath, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void EnsureCommand_UsesNamedFirewallRules_NoHostsOrDnsHijack()
    {
        var command = PrivacyFirewallPlan.BuildEnsureCommand();

        Assert.Contains("New-NetFirewallRule", command);
        Assert.Contains("-Direction Outbound -Action Block", command);
        Assert.Contains("-Program $program", command);
        Assert.DoesNotContain("RemoteAddress", command);
        Assert.DoesNotContain("Set-DnsClient", command);
        Assert.DoesNotContain("drivers\\etc\\hosts", command.ToLowerInvariant());
        Assert.All(PrivacyFirewallPlan.TelemetryRules, r => Assert.Contains(r.Name, command));
    }

    [Fact]
    public void RemoveCommand_TargetsOnlyExactAurumRuleNames()
    {
        var command = PrivacyFirewallPlan.BuildRemoveCommand();

        Assert.Contains("Remove-NetFirewallRule -Name $names", command);
        Assert.All(PrivacyFirewallPlan.TelemetryRules, r => Assert.Contains(r.Name, command));
        Assert.DoesNotContain("*", command);
    }

    [Fact]
    public void ParseRuleEnabledLines_ReadsPowerShellOutput()
    {
        var first = PrivacyFirewallPlan.TelemetryRules[0].Name;
        var second = PrivacyFirewallPlan.TelemetryRules[1].Name;

        var parsed = PrivacyFirewallPlan.ParseRuleEnabledLines($"{first}|True\r\n{second}|False\r\njunk");

        Assert.True(parsed[first]);
        Assert.False(parsed[second]);
    }

    [Fact]
    public void BuildReport_GatesBlockAndRemoveButtons()
    {
        var none = PrivacyFirewallPlan.BuildReport(new Dictionary<string, bool>(), queryOk: true);
        Assert.True(none.CanBlock);
        Assert.False(none.CanRemove);

        var all = PrivacyFirewallPlan.BuildReport(
            PrivacyFirewallPlan.TelemetryRules.ToDictionary(r => r.Name, _ => true),
            queryOk: true);
        Assert.True(all.AllBlocking);
        Assert.False(all.CanBlock);
        Assert.True(all.CanRemove);

        var failed = PrivacyFirewallPlan.BuildReport(new Dictionary<string, bool>(), queryOk: false);
        Assert.False(failed.CanBlock);
        Assert.False(failed.CanRemove);
        Assert.Contains("non lu", failed.StateDisplay);
    }
}

public class PrivacyReportTests
{
    private static PrivacySettingState St(string? live, bool present) =>
        new(new("x", "L", "A", PrivacyCategory.Telemetry, "HKCU", "K", "V", RegistryValueType.DWord, "0", "1"),
            live, present);

    [Fact]
    public void Counts_TallyEachBucket_AbsentCountsAsDefault()
    {
        var r = new PrivacyReport(new[] { St("0", true), St("1", true), St("5", true), St(null, false) });
        Assert.Equal(4, r.Total);
        Assert.Equal(1, r.HardenedCount);
        Assert.Equal(2, r.DefaultCount);   // present-default + absent
        Assert.Equal(1, r.CustomCount);
    }

    [Fact]
    public void AllHardened_TrueOnlyWhenEveryoneHardened()
    {
        Assert.True(new PrivacyReport(new[] { St("0", true), St("0", true) }).AllHardened);
        Assert.False(new PrivacyReport(new[] { St("0", true), St("1", true) }).AllHardened);
        Assert.False(new PrivacyReport(Array.Empty<PrivacySettingState>()).AllHardened);   // count>0 guard
    }

    [Fact]
    public void NoneHardened_TrueWhenNobodyHardened()
    {
        Assert.True(new PrivacyReport(new[] { St("1", true), St(null, false) }).NoneHardened);
        Assert.False(new PrivacyReport(new[] { St("0", true) }).NoneHardened);
    }
}

public class PrivacyServiceTests
{
    private static (PrivacyService svc, FakeRegistryService reg) New()
    {
        var reg = new FakeRegistryService(new EventLog());
        return (new PrivacyService(reg), reg);
    }

    private static string PathOf(PrivacySetting s) => $"{s.Hive}\\{s.Key}\\{s.ValueName}";

    [Fact]
    public async Task GetReport_ReadsSeededHardened_AndAbsentDefault()
    {
        var (svc, reg) = New();
        var ad = PrivacyCatalog.Find("advertising-id")!;
        reg.Seed(ad.Hive, ad.Key, ad.ValueName, "0");   // hardened

        var r = await svc.GetReportAsync();

        var adState = r.Settings.First(s => s.Id == "advertising-id");
        Assert.True(adState.IsPresent);
        Assert.True(adState.IsHardened);
        Assert.All(r.Settings.Where(s => s.Id != "advertising-id"), s => Assert.False(s.IsPresent));
        Assert.All(r.Settings.Where(s => s.Id != "advertising-id"), s => Assert.True(s.IsDefault));
    }

    [Fact]
    public async Task SetHardened_True_WritesHardenedValue()
    {
        var (svc, reg) = New();
        var ad = PrivacyCatalog.Find("advertising-id")!;

        var ok = await svc.SetHardenedAsync("advertising-id", harden: true);

        Assert.True(ok);
        Assert.Equal("0", reg.Store[PathOf(ad)]);
    }

    [Fact]
    public async Task SetHardened_False_WritesDefaultValue()
    {
        var (svc, reg) = New();
        var tel = PrivacyCatalog.Find("allow-telemetry")!;

        var ok = await svc.SetHardenedAsync("allow-telemetry", harden: false);

        Assert.True(ok);
        Assert.Equal("3", reg.Store[PathOf(tel)]);   // telemetry default = 3 (Full)
    }

    [Fact]
    public async Task SetHardened_False_DeletesPolicyValue_WhenDefaultIsNotConfigured()
    {
        var (svc, reg) = New();
        var recall = PrivacyCatalog.Find("recall-snapshots-device")!;
        reg.Seed(recall.Hive, recall.Key, recall.ValueName, "1");

        var ok = await svc.SetHardenedAsync("recall-snapshots-device", harden: false);

        Assert.True(ok);
        Assert.False(reg.Store.ContainsKey(PathOf(recall)));
    }

    [Fact]
    public async Task SetHardened_UnknownId_ReturnsFalse_AndWritesNothing()
    {
        var (svc, reg) = New();
        var ok = await svc.SetHardenedAsync("does-not-exist", harden: true);

        Assert.False(ok);
        Assert.Empty(reg.Store);
    }

    [Fact]
    public async Task ApplyAll_Harden_WritesEverySettingHardened()
    {
        var (svc, reg) = New();

        var ok = await svc.ApplyAllAsync(harden: true);

        Assert.True(ok);
        foreach (var s in PrivacyCatalog.Settings)
            Assert.Equal(s.HardenedValue, reg.Store[PathOf(s)]);
    }

    [Fact]
    public async Task ApplyAll_Restore_WritesEverySettingDefault()
    {
        var (svc, reg) = New();
        foreach (var s in PrivacyCatalog.Settings.Where(s => s.RestoreDeletesValue))
            reg.Seed(s.Hive, s.Key, s.ValueName, s.HardenedValue);

        var ok = await svc.ApplyAllAsync(harden: false);

        Assert.True(ok);
        foreach (var s in PrivacyCatalog.Settings)
        {
            if (s.RestoreDeletesValue)
                Assert.False(reg.Store.ContainsKey(PathOf(s)));
            else
                Assert.Equal(s.DefaultValue, reg.Store[PathOf(s)]);
        }
    }
}
