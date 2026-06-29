using System;
using System.Collections.Generic;
using System.Linq;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the curated <see cref="ScheduledTaskCatalog"/> behind the « Tâches planifiées » page. The load-bearing
/// honesty invariant: every task is a real, well-known Windows path; every category carries a non-empty French
/// label and advice; and exactly the genuinely-useful maintenance bucket is the one we DON'T recommend disabling
/// (so the page can say « à conserver » instead of pushing the common "disable defrag" myth).
/// </summary>
public class ScheduledTaskCatalogTests
{
    public static IEnumerable<object[]> AllCategories =>
        Enum.GetValues<ScheduledTaskCategory>().Select(c => new object[] { c });

    [Fact]
    public void Tasks_EveryEntry_HasAFullPathAndLabel()
    {
        Assert.NotEmpty(ScheduledTaskCatalog.Tasks);
        foreach (var t in ScheduledTaskCatalog.Tasks)
        {
            Assert.StartsWith(@"\", t.FullPath);                              // a real Task Scheduler path
            Assert.False(string.IsNullOrWhiteSpace(t.Label));
        }
    }

    [Fact]
    public void Tasks_FullPaths_AreDistinct()
    {
        var set = new HashSet<string>(ScheduledTaskCatalog.Tasks.Select(t => t.FullPath), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(ScheduledTaskCatalog.Tasks.Count, set.Count);
    }

    [Theory]
    [MemberData(nameof(AllCategories))]
    public void CategoryLabel_And_Advice_AreNonEmpty_ForEveryCategory(ScheduledTaskCategory category)
    {
        Assert.False(string.IsNullOrWhiteSpace(ScheduledTaskCatalog.CategoryLabel(category)));
        Assert.False(string.IsNullOrWhiteSpace(ScheduledTaskCatalog.Advice(category)));
    }

    [Theory]
    [MemberData(nameof(AllCategories))]
    public void RecommendedToDisable_IsTrue_ExceptForMaintenance(ScheduledTaskCategory category)
        => Assert.Equal(category != ScheduledTaskCategory.Maintenance, ScheduledTaskCatalog.RecommendedToDisable(category));

    [Fact]
    public void Catalog_IncludesTheMaintenanceTask_FlaggedKeep()
    {
        // The honesty counterweight: a genuinely useful task is present, and it is NOT recommended for disabling.
        Assert.Contains(ScheduledTaskCatalog.Tasks, t => t.Category == ScheduledTaskCategory.Maintenance);
        Assert.False(ScheduledTaskCatalog.RecommendedToDisable(ScheduledTaskCategory.Maintenance));
    }
}

/// <summary>
/// Pins <see cref="ScheduledTaskResolver.Resolve"/> — the pure join of the curated catalog with the live
/// "path → enabled?" map, plus its actionable-first ordering. The load-bearing honesty case: a catalog task with
/// no live state is <see cref="ScheduledTaskLiveState.Absent"/> (never silently treated as enabled or disabled),
/// and an enabled « à conserver » task ranks below the actionable telemetry rather than masquerading as bloat.
/// </summary>
public class ScheduledTaskResolverTests
{
    private static readonly ScheduledTaskInfo Tele = new(@"\T\Tele", "Tele", ScheduledTaskCategory.Telemetry);
    private static readonly ScheduledTaskInfo Maint = new(@"\T\Maint", "Maint", ScheduledTaskCategory.Maintenance);
    private static readonly ScheduledTaskInfo Feedback = new(@"\T\Fb", "Fb", ScheduledTaskCategory.Feedback);
    private static readonly ScheduledTaskInfo Missing = new(@"\T\Gone", "Gone", ScheduledTaskCategory.Diagnostics);

    private static readonly IReadOnlyList<ScheduledTaskInfo> Catalog = new[] { Tele, Maint, Feedback, Missing };

    private static IReadOnlyDictionary<string, bool> Map(params (string Path, bool Enabled)[] rows)
        => rows.ToDictionary(r => r.Path, r => r.Enabled, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Resolve_PathInMap_ResolvesEnabledOrDisabled()
    {
        var entries = ScheduledTaskResolver.Resolve(Catalog, Map((Tele.FullPath, true), (Feedback.FullPath, false)));

        Assert.Equal(ScheduledTaskLiveState.Enabled, entries.Single(e => e.Info == Tele).State);
        Assert.Equal(ScheduledTaskLiveState.Disabled, entries.Single(e => e.Info == Feedback).State);
    }

    [Fact]
    public void Resolve_PathNotInMap_IsAbsent_NotInvented()
    {
        var absent = ScheduledTaskResolver.Resolve(Catalog, Map()).Single(e => e.Info == Missing);

        Assert.Equal(ScheduledTaskLiveState.Absent, absent.State);
        Assert.False(absent.IsPresent);
        Assert.False(absent.ShowToggle);   // an absent task offers no toggle to honour
    }

    [Fact]
    public void Resolve_EmptyMap_EverythingAbsent()
    {
        var entries = ScheduledTaskResolver.Resolve(Catalog, Map());
        Assert.All(entries, e => Assert.False(e.IsPresent));
    }

    [Fact]
    public void Resolve_OrdersActionable_ThenKeep_ThenDisabled_ThenAbsent()
    {
        // Tele on (actionable), Maint on (à conserver), Feedback off (disabled), Missing not present (absent).
        var entries = ScheduledTaskResolver.Resolve(Catalog,
            Map((Tele.FullPath, true), (Maint.FullPath, true), (Feedback.FullPath, false)));

        Assert.Equal(new[] { "Tele", "Maint", "Fb", "Gone" }, entries.Select(e => e.Label).ToArray());
    }

    [Fact]
    public void Entry_EnabledMaintenance_ShowsKeepBadge_AndIsNotRecommendedOff()
    {
        var maint = ScheduledTaskResolver.Resolve(Catalog, Map((Maint.FullPath, true))).Single(e => e.Info == Maint);

        Assert.True(maint.IsEnabled);
        Assert.True(maint.ShowKeepBadge);
        Assert.False(maint.RecommendedToDisable);
    }

    [Fact]
    public void Report_EnabledRecommendedCount_ExcludesMaintenance()
    {
        // Both Tele and Maint are enabled, but only the telemetry one is "still leaking" — the count must say 1.
        var report = new ScheduledTaskReport(
            ScheduledTaskResolver.Resolve(Catalog, Map((Tele.FullPath, true), (Maint.FullPath, true))),
            QueryOk: true);

        Assert.Equal(1, report.EnabledRecommendedCount);
        Assert.Equal(2, report.PresentCount);
    }
}
