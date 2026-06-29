using System;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

public class PagefileModeInfoTests
{
    [Theory]
    [InlineData(PagefileMode.Automatic,         "Géré automatiquement par Windows")]
    [InlineData(PagefileMode.SystemManagedSize, "Taille gérée par le système")]
    [InlineData(PagefileMode.CustomFixed,       "Taille personnalisée")]
    [InlineData(PagefileMode.Disabled,          "Désactivé")]
    [InlineData(PagefileMode.Unknown,           "Indéterminé")]
    public void Describe_Cases(PagefileMode mode, string expected)
        => Assert.Equal(expected, PagefileModeInfo.Describe(mode));
}

public class PagefileModeClassifierTests
{
    // "Automatic" always wins — the master checkbox overrides any per-file size facts.
    [Theory]
    [InlineData(true, false, false, PagefileMode.Automatic)]
    [InlineData(true, true, true, PagefileMode.Automatic)]
    public void Automatic_Wins(bool auto, bool active, bool configured, PagefileMode expected)
        => Assert.Equal(expected, PagefileModeClassifier.Classify(auto, active, configured));

    // With the checkbox off, no active pagefile means it is genuinely disabled.
    [Fact]
    public void NoActive_IsDisabled()
        => Assert.Equal(PagefileMode.Disabled, PagefileModeClassifier.Classify(false, anyActive: false, anyConfiguredSize: false));

    // An explicit initial/max with the checkbox off is a custom fixed size; otherwise the system sizes it.
    [Fact]
    public void Active_WithConfiguredSize_IsCustomFixed()
        => Assert.Equal(PagefileMode.CustomFixed, PagefileModeClassifier.Classify(false, anyActive: true, anyConfiguredSize: true));

    [Fact]
    public void Active_WithoutConfiguredSize_IsSystemManaged()
        => Assert.Equal(PagefileMode.SystemManagedSize, PagefileModeClassifier.Classify(false, anyActive: true, anyConfiguredSize: false));
}

public class PagefileSizeTests
{
    [Theory]
    [InlineData(-1L, "—")]      // unknown must never read as a fabricated 0
    [InlineData(0L, "0 o")]     // a real, measured zero is meaningful (pagefile untouched)
    [InlineData(1L, "1 Mo")]
    [InlineData(512L, "512 Mo")]
    [InlineData(1024L, "1 Go")]
    [InlineData(2048L, "2 Go")]
    [InlineData(1536L, "1,5 Go")]   // fr-FR decimal comma, pinned by ByteSize's fixed culture
    public void Format_Cases(long megabytes, string expected)
        => Assert.Equal(expected, PagefileSize.Format(megabytes));
}

public class PagefileEntryTests
{
    [Theory]
    [InlineData(@"C:\pagefile.sys", "C:")]
    [InlineData(@"D:\pagefile.sys", "D:")]
    public void Drive_ParsedFromPath(string path, string expected)
        => Assert.Equal(expected, new PagefileEntry(path, 0, 0, 0, -1, -1).Drive);

    [Fact]
    public void Drive_And_Path_Dash_WhenBlank()
    {
        var e = new PagefileEntry("", 0, 0, 0, -1, -1);
        Assert.Equal("—", e.Drive);
        Assert.Equal("—", e.PathDisplay);
    }

    [Fact]
    public void Live_Sizes_Format_AndDistinguishZeroFromUnknown()
    {
        var e = new PagefileEntry(@"C:\pagefile.sys", AllocatedMb: 2048, CurrentMb: 0, PeakMb: -1, InitialMb: -1, MaxMb: -1);
        Assert.Equal("2 Go", e.AllocatedDisplay);
        Assert.Equal("0 o", e.CurrentDisplay);   // measured zero usage, not unknown
        Assert.Equal("—", e.PeakDisplay);        // genuinely unreadable
    }

    [Theory]
    [InlineData(0L, 0L, false)]
    [InlineData(-1L, -1L, false)]
    [InlineData(1024L, 0L, true)]
    [InlineData(0L, 2048L, true)]
    public void HasConfiguredSize_OnlyWhenExplicitPositive(long initial, long max, bool expected)
        => Assert.Equal(expected, new PagefileEntry(@"C:\pagefile.sys", 0, 0, 0, initial, max).HasConfiguredSize);

    [Fact]
    public void Configured_Dash_WhenUnknown()
        => Assert.Equal("—", new PagefileEntry(@"C:\pagefile.sys", 0, 0, 0, -1, -1).ConfiguredDisplay);

    [Fact]
    public void Configured_SystemManaged_WhenBothZero()
        => Assert.Equal("Géré par le système", new PagefileEntry(@"C:\pagefile.sys", 0, 0, 0, 0, 0).ConfiguredDisplay);

    [Fact]
    public void Configured_Range_WhenFixed()
        => Assert.Equal("1 Go – 2 Go", new PagefileEntry(@"C:\pagefile.sys", 0, 0, 0, 1024, 2048).ConfiguredDisplay);
}

public class PagefileAdvisorTests
{
    [Fact]
    public void Automatic_IsOk_AndRecommended()
    {
        var r = PagefileAdvisor.Assess(PagefileMode.Automatic);
        Assert.Equal(PagefileVerdict.Ok, r.Verdict);
        Assert.Contains("recommandé", r.Headline);
    }

    // Load-bearing honesty: disabling is WARNED (apps can crash), and the felt gain is called negligible.
    [Fact]
    public void Disabled_IsWarning_AndHonestAboutCrashes()
    {
        var r = PagefileAdvisor.Assess(PagefileMode.Disabled);
        Assert.Equal(PagefileVerdict.Warning, r.Verdict);
        Assert.Contains("déconseillé", r.Headline);
        Assert.Contains("planter", r.Detail);
        Assert.Contains("négligeable", r.Detail);
    }

    // Load-bearing honesty: a fixed pagefile is NEVER sold as an FPS boost.
    [Fact]
    public void CustomFixed_IsInfo_AndDeniesFpsGain()
    {
        var r = PagefileAdvisor.Assess(PagefileMode.CustomFixed);
        Assert.Equal(PagefileVerdict.Info, r.Verdict);
        Assert.Contains("Ce n'est pas un gain de FPS", r.Detail);
    }

    [Fact]
    public void SystemManaged_IsInfo()
        => Assert.Equal(PagefileVerdict.Info, PagefileAdvisor.Assess(PagefileMode.SystemManagedSize).Verdict);

    [Fact]
    public void Unknown_IsInfo_AndAdmitsItCannotRead()
    {
        var r = PagefileAdvisor.Assess(PagefileMode.Unknown);
        Assert.Equal(PagefileVerdict.Info, r.Verdict);
        Assert.Contains("indéterminé", r.Headline);
    }
}

public class PagefileActionOutcomeTests
{
    // Verified true: the write took, and the message is honest that the resize lands at reboot.
    [Fact]
    public void Verified_True_IsOk_AndMentionsReboot()
    {
        var o = PagefileActionOutcome.FromVerified(nowAutomatic: true);
        Assert.True(o.Ok);
        Assert.Contains("redémarrage", o.Message);
    }

    // Verified false: a silently-refused WMI Put reports failure, never a fabricated success.
    [Fact]
    public void Verified_False_IsFailure()
    {
        var o = PagefileActionOutcome.FromVerified(nowAutomatic: false);
        Assert.False(o.Ok);
        Assert.Contains("refusée", o.Message);
    }

    [Fact]
    public void Failed_IsNotOk()
        => Assert.False(PagefileActionOutcome.Failed.Ok);
}

public class PagefileReportTests
{
    private static PagefileEntry Entry(
        string path = @"C:\pagefile.sys", long allocated = 1024, long current = 256, long peak = 512,
        long initial = -1, long max = -1)
        => new(path, allocated, current, peak, initial, max);

    [Fact]
    public void QueryFailed_IsUnknown_AndHonest()
    {
        var rep = PagefileReport.From(queryOk: false, automaticManaged: false, entries: Array.Empty<PagefileEntry>());
        Assert.False(rep.QueryOk);
        Assert.Equal(PagefileMode.Unknown, rep.Mode);
        Assert.Equal("Lecture de la configuration impossible.", rep.Headline);
        Assert.True(rep.VerdictInfo);
    }

    [Fact]
    public void Failed_Singleton_MatchesQueryFailed()
    {
        Assert.False(PagefileReport.Failed.QueryOk);
        Assert.Equal(PagefileMode.Unknown, PagefileReport.Failed.Mode);
    }

    [Fact]
    public void Empty_WithCheckboxOff_IsDisabled_AndOffersRestore()
    {
        var rep = PagefileReport.From(queryOk: true, automaticManaged: false, entries: Array.Empty<PagefileEntry>());
        Assert.Equal(PagefileMode.Disabled, rep.Mode);
        Assert.False(rep.HasEntries);
        Assert.Equal("Aucun fichier d'échange actif (désactivé).", rep.Headline);
        Assert.True(rep.VerdictWarn);
        Assert.True(rep.CanRestoreAutomatic);   // not auto-managed → the one write is offered
    }

    [Fact]
    public void Automatic_Wins_OverConfiguredSize_AndHidesRestore()
    {
        // Even with a fixed-size entry, the master checkbox makes the mode Automatic and the write a no-op (hidden).
        var rep = PagefileReport.From(queryOk: true, automaticManaged: true,
            entries: new[] { Entry(allocated: 1024, initial: 1024, max: 2048) });
        Assert.Equal(PagefileMode.Automatic, rep.Mode);
        Assert.True(rep.VerdictOk);
        Assert.False(rep.CanRestoreAutomatic);   // already auto-managed → never a no-op button
    }

    [Fact]
    public void Active_FixedSize_IsCustomFixed()
    {
        var rep = PagefileReport.From(queryOk: true, automaticManaged: false,
            entries: new[] { Entry(allocated: 4096, initial: 4096, max: 4096) });
        Assert.Equal(PagefileMode.CustomFixed, rep.Mode);
        Assert.True(rep.VerdictInfo);
        Assert.True(rep.CanRestoreAutomatic);
    }

    [Fact]
    public void Active_NoConfiguredSize_IsSystemManaged()
    {
        var rep = PagefileReport.From(queryOk: true, automaticManaged: false,
            entries: new[] { Entry(allocated: 2048, initial: 0, max: 0) });
        Assert.Equal(PagefileMode.SystemManagedSize, rep.Mode);
    }

    [Fact]
    public void Single_Entry_Summarised_Singular()
    {
        var rep = PagefileReport.From(queryOk: true, automaticManaged: true,
            entries: new[] { Entry(allocated: 1024) });
        Assert.Equal(1, rep.EntryCount);
        Assert.Equal("1 Go", rep.TotalAllocatedDisplay);
        Assert.Contains("1 fichier d'échange", rep.Headline);
        Assert.Contains("1 Go", rep.Headline);
    }

    [Fact]
    public void Multiple_Entries_SumAllocation_Plural()
    {
        var rep = PagefileReport.From(queryOk: true, automaticManaged: true,
            entries: new[] { Entry(@"C:\pagefile.sys", allocated: 1024), Entry(@"D:\pagefile.sys", allocated: 1024) });
        Assert.Equal(2, rep.EntryCount);
        Assert.Equal("2 Go", rep.TotalAllocatedDisplay);   // 1024 + 1024 MB summed
        Assert.Contains("2 fichiers d'échange", rep.Headline);
    }

    // Unknown/negative allocations must not poison the total — only real positive allocations are summed.
    [Fact]
    public void Total_Ignores_UnknownAllocations()
    {
        var rep = PagefileReport.From(queryOk: true, automaticManaged: false,
            entries: new[] { Entry(allocated: 2048, initial: 0, max: 0), Entry(@"D:\pagefile.sys", allocated: -1) });
        Assert.Equal("2 Go", rep.TotalAllocatedDisplay);
    }

    // Auto-managed yet no pagefile materialised yet: not "disabled", just none active — the headline stays honest.
    [Fact]
    public void Automatic_ButNoActivePagefile_HeadlineHonest()
        => Assert.Equal("Aucun fichier d'échange actif détecté.",
            PagefileReport.From(queryOk: true, automaticManaged: true, entries: Array.Empty<PagefileEntry>()).Headline);
}
