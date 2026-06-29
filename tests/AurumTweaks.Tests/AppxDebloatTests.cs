using System;
using System.Collections.Generic;
using System.Linq;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the curated <see cref="AppxCatalog"/> behind the « Applications préinstallées » page. The load-bearing
/// honesty invariant: the list is a hand-picked allow-list of consumer bloat that NEVER contains a system-critical
/// package (the Store, shell, runtimes) — because blindly removing those bricks Windows; and exactly the genuinely
/// useful Xbox/gaming bucket is the one we DON'T recommend removing (so the page can say « à conserver » instead of
/// pushing a gamer to delete their Game Bar / Game Pass app).
/// </summary>
public class AppxCatalogTests
{
    public static IEnumerable<object[]> AllCategories =>
        Enum.GetValues<AppxCategory>().Select(c => new object[] { c });

    [Fact]
    public void Apps_EveryEntry_HasANameAndLabel()
    {
        Assert.NotEmpty(AppxCatalog.Apps);
        foreach (var a in AppxCatalog.Apps)
        {
            Assert.False(string.IsNullOrWhiteSpace(a.Name));
            Assert.False(string.IsNullOrWhiteSpace(a.Label));
            Assert.DoesNotContain(',', a.Name);   // a package identity name is dotted, never comma'd
        }
    }

    [Fact]
    public void Apps_Names_AreDistinct()
    {
        var set = new HashSet<string>(AppxCatalog.Apps.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(AppxCatalog.Apps.Count, set.Count);
    }

    [Theory]
    [MemberData(nameof(AllCategories))]
    public void CategoryLabel_And_Advice_AreNonEmpty_ForEveryCategory(AppxCategory category)
    {
        Assert.False(string.IsNullOrWhiteSpace(AppxCatalog.CategoryLabel(category)));
        Assert.False(string.IsNullOrWhiteSpace(AppxCatalog.Advice(category)));
    }

    [Theory]
    [MemberData(nameof(AllCategories))]
    public void RecommendedToRemove_IsTrue_ExceptForGaming(AppxCategory category)
        => Assert.Equal(category != AppxCategory.Gaming, AppxCatalog.RecommendedToRemove(category));

    [Fact]
    public void Catalog_IncludesTheGamingBucket_FlaggedKeep()
    {
        // The honesty counterweight: a genuinely useful app is present, and it is NOT recommended for removal.
        Assert.Contains(AppxCatalog.Apps, a => a.Category == AppxCategory.Gaming);
        Assert.False(AppxCatalog.RecommendedToRemove(AppxCategory.Gaming));
    }

    // Packages that keep Windows (and this app) working: the Store itself — needed to reinstall ANYTHING — the shell,
    // security UI, and the shared runtimes. None of these may ever appear in a debloat allow-list.
    private static readonly string[] CriticalNames =
    {
        "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller",
        "Microsoft.SecHealthUI", "Microsoft.Windows.ShellExperienceHost", "Microsoft.Windows.StartMenuExperienceHost",
        "Microsoft.Windows.Photos", "Microsoft.WindowsCalculator", "Microsoft.WindowsNotepad", "Microsoft.Paint",
        "Microsoft.WindowsTerminal", "Microsoft.AAD.BrokerPlugin", "Microsoft.AccountsControl", "Microsoft.LockApp",
        "Windows.immersivecontrolpanel",
    };

    // Fragments that mark a runtime/shell component regardless of its exact versioned name.
    private static readonly string[] CriticalFragments =
    {
        "VCLibs", "UI.Xaml", "NET.Native", "ShellExperience", "StartMenu", "SecHealth", "WindowsStore", "DesktopAppInstaller",
    };

    [Fact]
    public void Catalog_ExcludesSystemCriticalPackages()
    {
        foreach (var app in AppxCatalog.Apps)
        {
            Assert.DoesNotContain(CriticalNames, n => n.Equals(app.Name, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(CriticalFragments, f => app.Name.Contains(f, StringComparison.OrdinalIgnoreCase));
        }
    }
}

/// <summary>
/// Pins <see cref="AppxStateParser"/> — the read of the CSV that
/// <c>Get-AppxPackage | Select Name,PackageFullName,NonRemovable | ConvertTo-Csv</c> emits behind the page. The
/// honesty points: we key on the version-independent <c>Name</c> (so the catalog matches across builds) yet carry
/// the versioned <c>PackageFullName</c> verbatim (exactly what <c>Remove-AppxPackage -Package</c> wants), an app is
/// only « non supprimable » when Windows says so (the invariant <c>NonRemovable</c> bool — never guessed), and a
/// package we can't find is simply absent from the map, never invented as installed.
/// </summary>
public class AppxStateParserTests
{
    // A representative ConvertTo-Csv block: the quoted header row + three packages, one marked non-removable.
    private const string SampleCsv = @"""Name"",""PackageFullName"",""NonRemovable""
""Microsoft.BingNews"",""Microsoft.BingNews_4.55.2_x64__8wekyb3d8bbwe"",""False""
""Microsoft.SecHealthUI"",""Microsoft.SecHealthUI_1000.1_x64__8wekyb3d8bbwe"",""True""
""king.com.CandyCrushSaga"",""king.com.CandyCrushSaga_1.2_x64__kgqvnymyfvs32"",""False""";

    [Fact]
    public void Parse_KeysOnName_AndCarriesTheVersionedFullName()
    {
        var map = AppxStateParser.Parse(SampleCsv);

        Assert.True(map.ContainsKey("Microsoft.BingNews"));
        Assert.Equal("Microsoft.BingNews_4.55.2_x64__8wekyb3d8bbwe", map["Microsoft.BingNews"].PackageFullName);
    }

    [Fact]
    public void Parse_ReadsNonRemovableFromWindows()
    {
        var map = AppxStateParser.Parse(SampleCsv);

        Assert.False(map["Microsoft.BingNews"].NonRemovable);
        Assert.True(map["Microsoft.SecHealthUI"].NonRemovable);   // Windows protects it
    }

    [Fact]
    public void Parse_SkipsTheColumnHeaderRow()
        => Assert.False(AppxStateParser.Parse(SampleCsv).ContainsKey("Name"));

    [Theory]
    [InlineData("True", true)]
    [InlineData("true", true)]    // case-insensitive
    [InlineData("TRUE", true)]
    [InlineData("1", true)]       // numeric form tolerated
    [InlineData("False", false)]
    [InlineData("", false)]       // older builds leave it blank → removable (safe default)
    [InlineData("0", false)]
    [InlineData("Ready", false)]  // any non-true token → removable
    public void Parse_NonRemovableIsRecognisedTrue_EverythingElseRemovable(string flag, bool expectedNonRemovable)
    {
        var map = AppxStateParser.Parse($"\"App.X\",\"App.X_1_x64__p\",\"{flag}\"");
        Assert.Equal(expectedNonRemovable, map["App.X"].NonRemovable);
    }

    [Fact]
    public void Parse_MissingNonRemovableColumn_DefaultsRemovable()
    {
        // Two columns only (Name, PackageFullName) — the bool is absent, so we treat the app as removable.
        var map = AppxStateParser.Parse("\"App.X\",\"App.X_1_x64__p\"");
        Assert.True(map.ContainsKey("App.X"));
        Assert.False(map["App.X"].NonRemovable);
    }

    [Fact]
    public void Parse_HandlesCrlfLineEndings()
    {
        var crlf = "\"Name\",\"PackageFullName\",\"NonRemovable\"\r\n\"App.X\",\"App.X_1_x64__p\",\"True\"";
        var map = AppxStateParser.Parse(crlf);
        Assert.True(map["App.X"].NonRemovable);   // the trailing '\r' didn't corrupt the bool read
    }

    [Fact]
    public void Parse_IgnoresStrayTypeHeaderLine()
        => Assert.Empty(AppxStateParser.Parse("#TYPE Selected.Microsoft.Windows.Appx.PackageManager.Commands.AppxPackage"));

    [Fact]
    public void Parse_KeyLookupIsCaseInsensitive()
    {
        var map = AppxStateParser.Parse("\"Microsoft.BingNews\",\"Microsoft.BingNews_1_x64__p\",\"False\"");
        Assert.True(map.ContainsKey("microsoft.bingnews"));
    }

    [Fact]
    public void Parse_BlankNameOrFullName_IsSkipped()
    {
        Assert.Empty(AppxStateParser.Parse("\"\",\"App.X_1_x64__p\",\"False\""));   // no name
        Assert.Empty(AppxStateParser.Parse("\"App.X\",\"\",\"False\""));            // no full name → can't uninstall
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Parse_EmptyOrNull_ReturnsEmpty(string? csv)
        => Assert.Empty(AppxStateParser.Parse(csv));
}

/// <summary>
/// Pins <see cref="AppxResolver.Resolve"/> — the pure join of the curated catalog with the live "name → package"
/// map, plus its actionable-first ordering — and the <see cref="AppxReport"/> counts. The load-bearing honesty
/// cases: a catalog app with no live package is <see cref="AppxLiveState.Absent"/> with NO uninstall offered (never
/// silently treated as installed), an installed « à conserver » Xbox app ranks below the actionable bloat rather
/// than masquerading as junk, and a Windows-protected (NonRemovable) app shows but never gets a dead "Supprimer".
/// </summary>
public class AppxResolverTests
{
    private static readonly AppxInfo Promo = new("X.Promo", "Promo", AppxCategory.PromoGames);
    private static readonly AppxInfo Keep = new("X.Xbox", "Xbox", AppxCategory.Gaming);
    private static readonly AppxInfo News = new("X.News", "News", AppxCategory.News);
    private static readonly AppxInfo Gone = new("X.Gone", "Gone", AppxCategory.Media);

    private static readonly IReadOnlyList<AppxInfo> Catalog = new[] { Promo, Keep, News, Gone };

    private static IReadOnlyDictionary<string, AppxLivePackage> Map(params (string Name, string FullName, bool NonRemovable)[] rows)
        => rows.ToDictionary(r => r.Name, r => new AppxLivePackage(r.Name, r.FullName, r.NonRemovable), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Resolve_NameInMap_IsInstalled_CarryingFullName()
    {
        var promo = AppxResolver.Resolve(Catalog, Map((Promo.Name, "X.Promo_1_x64__p", false))).Single(e => e.Info == Promo);

        Assert.Equal(AppxLiveState.Installed, promo.State);
        Assert.True(promo.IsInstalled);
        Assert.Equal("X.Promo_1_x64__p", promo.PackageFullName);
        Assert.True(promo.ShowRemove);
    }

    [Fact]
    public void Resolve_NameNotInMap_IsAbsent_NotInvented()
    {
        var absent = AppxResolver.Resolve(Catalog, Map()).Single(e => e.Info == Gone);

        Assert.Equal(AppxLiveState.Absent, absent.State);
        Assert.False(absent.IsInstalled);
        Assert.Equal(string.Empty, absent.PackageFullName);
        Assert.False(absent.ShowRemove);     // an absent app offers no uninstall
        Assert.True(absent.ShowAbsentBadge);
    }

    [Fact]
    public void Resolve_EmptyMap_EverythingAbsent()
        => Assert.All(AppxResolver.Resolve(Catalog, Map()), e => Assert.False(e.IsInstalled));

    [Fact]
    public void Resolve_OrdersActionableBloat_ThenKeep_ThenProtected_ThenAbsent()
    {
        // Promo installed removable (actionable), Xbox installed (à conserver), News installed but protected,
        // Gone not present (absent).
        var entries = AppxResolver.Resolve(Catalog,
            Map((Promo.Name, "X.Promo_1_x64__p", false),
                (Keep.Name, "X.Xbox_1_x64__p", false),
                (News.Name, "X.News_1_x64__p", true)));

        Assert.Equal(new[] { "Promo", "Xbox", "News", "Gone" }, entries.Select(e => e.Label).ToArray());
    }

    [Fact]
    public void Entry_InstalledGaming_ShowsKeepBadge_NotRecommendedRemove_ButStillRemovable()
    {
        var xbox = AppxResolver.Resolve(Catalog, Map((Keep.Name, "X.Xbox_1_x64__p", false))).Single(e => e.Info == Keep);

        Assert.True(xbox.IsInstalled);
        Assert.True(xbox.ShowKeepBadge);
        Assert.False(xbox.RecommendedToRemove);
        Assert.True(xbox.ShowRemove);          // the user may still choose to remove it — it's just flagged « à conserver »
    }

    [Fact]
    public void Entry_Protected_HidesRemove_ShowsSystemBadge()
    {
        var news = AppxResolver.Resolve(Catalog, Map((News.Name, "X.News_1_x64__p", true))).Single(e => e.Info == News);

        Assert.True(news.NonRemovable);
        Assert.False(news.ShowRemove);         // no dead button for a package Windows won't let us uninstall
        Assert.True(news.ShowSystemBadge);
    }

    [Fact]
    public void Report_RemovableRecommendedCount_ExcludesGamingAndProtected()
    {
        // Promo (actionable), Xbox (keep), News (recommended but protected) all installed — only Promo is "still bloat".
        var report = new AppxReport(
            AppxResolver.Resolve(Catalog,
                Map((Promo.Name, "X.Promo_1_x64__p", false),
                    (Keep.Name, "X.Xbox_1_x64__p", false),
                    (News.Name, "X.News_1_x64__p", true))),
            QueryOk: true);

        Assert.Equal(1, report.RemovableRecommendedCount);
        Assert.Equal(3, report.InstalledCount);
    }
}
