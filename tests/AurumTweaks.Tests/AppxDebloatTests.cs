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
            Assert.False(AppxCatalog.IsSystemCritical(app.Name));
        }
    }

    [Theory]
    [InlineData("Microsoft.WindowsStore")]
    [InlineData("Microsoft.VCLibs.140.00")]
    [InlineData("")]
    public void IsSystemCritical_FlagsProtectedNamesAndFragments(string packageName)
        => Assert.True(AppxCatalog.IsSystemCritical(packageName));
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
    public void Parse_ReadsFrameworkResourceAndSignatureKind_WhenPresent()
    {
        var map = AppxStateParser.Parse("\"App.Framework\",\"App.Framework_1_x64__p\",\"False\",\"True\",\"False\",\"Store\"");

        var app = map["App.Framework"];
        Assert.True(app.IsFramework);
        Assert.False(app.IsResourcePackage);
        Assert.Equal("Store", app.SignatureKind);
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
    public void Entry_RemovableRemoval_IsMarkedNonReversibleInAurum()
    {
        var promo = AppxResolver.Resolve(Catalog, Map((Promo.Name, "X.Promo_1_x64__p", false))).Single(e => e.Info == Promo);
        var absent = AppxResolver.Resolve(Catalog, Map()).Single(e => e.Info == Promo);

        Assert.True(promo.ShowNonReversibleRemoval);
        Assert.Contains("non réversible", promo.RemovalReversibilityDisplay);
        Assert.False(absent.ShowNonReversibleRemoval);
        Assert.Equal(string.Empty, absent.RemovalReversibilityDisplay);
    }

    [Fact]
    public void HiddenPackages_RevealsOnlyNonCatalogNonCriticalNonFrameworkPackages()
    {
        var live = new Dictionary<string, AppxLivePackage>(StringComparer.OrdinalIgnoreCase)
        {
            [Promo.Name] = new(Promo.Name, "X.Promo_1_x64__p", false),
            ["Vendor.HiddenTool"] = new("Vendor.HiddenTool", "Vendor.HiddenTool_1_x64__p", false),
            ["Microsoft.WindowsStore"] = new("Microsoft.WindowsStore", "Microsoft.WindowsStore_1_x64__p", false),
            ["Microsoft.VCLibs.140.00"] = new("Microsoft.VCLibs.140.00", "Microsoft.VCLibs.140.00_1_x64__p", false),
            ["Vendor.Framework"] = new("Vendor.Framework", "Vendor.Framework_1_x64__p", false, IsFramework: true),
            ["Vendor.Resource"] = new("Vendor.Resource", "Vendor.Resource_1_x64__p", false, IsResourcePackage: true),
        };

        var hidden = AppxResolver.HiddenPackages(Catalog, live);

        var only = Assert.Single(hidden);
        Assert.Equal("Vendor.HiddenTool", only.Name);
        Assert.Contains("aucune suppression", only.LimitDisplay);
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

    [Fact]
    public void Report_HiddenCount_ComesFromHiddenRows()
    {
        var report = new AppxReport(Array.Empty<AppxEntry>(), QueryOk: true,
            new[] { new HiddenAppxEntry(new AppxLivePackage("Vendor.Hidden", "Vendor.Hidden_1_x64__p", false)) });

        Assert.Equal(1, report.HiddenCount);
        Assert.Single(report.HiddenPackageRows);
    }
}

public class WingetParserTests
{
    private static string Table(bool upgrades, params (string Name, string Id, string Version, string Available, string Source)[] rows)
    {
        var lines = new List<string>
        {
            upgrades
                ? $"{"Name",-26}{"Id",-30}{"Version",-14}{"Available",-14}Source"
                : $"{"Name",-26}{"Id",-30}{"Version",-14}Source",
            new('-', 90)
        };
        foreach (var r in rows)
        {
            lines.Add(upgrades
                ? $"{r.Name,-26}{r.Id,-30}{r.Version,-14}{r.Available,-14}{r.Source}"
                : $"{r.Name,-26}{r.Id,-30}{r.Version,-14}{r.Source}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    [Fact]
    public void ListParser_ReadsInstalledIds_FromWingetTable()
    {
        var text = Table(false,
            ("7-Zip", "7zip.7zip", "24.09", "", "winget"),
            ("Microsoft PowerToys", "Microsoft.PowerToys", "0.83.0", "", "winget"));

        var ids = WingetListParser.ParseInstalledIds(text);

        Assert.Contains("7zip.7zip", ids);
        Assert.Contains("microsoft.powertoys", ids);
    }

    [Fact]
    public void UpgradeParser_ReadsOnlyRowsWithAvailableVersion()
    {
        var text = Table(true,
            ("Microsoft PowerToys", "Microsoft.PowerToys", "0.83.0", "0.84.0", "winget"),
            ("OBS Studio", "OBSProject.OBSStudio", "30.0", "30.1", "winget"));

        var upgrades = WingetUpgradeParser.Parse(text);

        Assert.Equal(2, upgrades.Count);
        Assert.Equal("Microsoft.PowerToys", upgrades[0].Id);
        Assert.Equal("0.83.0 → 0.84.0", upgrades[0].VersionDisplay);
    }

    [Fact]
    public void UpgradeParser_EmptyOrNoHeader_ReturnsEmpty()
    {
        Assert.Empty(WingetUpgradeParser.Parse(null));
        Assert.Empty(WingetUpgradeParser.Parse("No installed package found matching input criteria."));
    }
}

public class WingetPlanTests
{
    [Fact]
    public void BuildInstallOptions_MarksInstalledAndSortsMissingFirst()
    {
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "7zip.7zip" };
        var options = WingetPlan.BuildInstallOptions(WingetCatalog.Packages, installed);

        Assert.True(options.Single(o => o.Id == "7zip.7zip").Installed);
        Assert.False(options.First().Installed);
    }

    [Fact]
    public void AllowedInstallIds_FiltersUnknownAndDedupes()
    {
        var ids = WingetPlan.AllowedInstallIds(WingetCatalog.Packages,
            new[] { "7zip.7zip", "unknown.Tool", "7ZIP.7ZIP", "  Microsoft.PowerToys  " });

        Assert.Equal(new[] { "7zip.7zip", "Microsoft.PowerToys" }, ids);
    }

    [Fact]
    public void ListedUpgradeIds_FiltersToDisplayedUpgradeList()
    {
        var listed = new[]
        {
            new WingetUpgradeEntry("PowerToys", "Microsoft.PowerToys", "0.83", "0.84", "winget")
        };

        var ids = WingetPlan.ListedUpgradeIds(listed, new[] { "Microsoft.PowerToys", "7zip.7zip" });

        Assert.Equal(new[] { "Microsoft.PowerToys" }, ids);
    }

    [Fact]
    public void WingetActionReport_SummaryReportsPartialFailure()
    {
        var report = new WingetActionReport(2, 1, new[] { "Bad.Package" });

        Assert.False(report.AllSucceeded);
        Assert.Contains("Bad.Package", report.Summary);
    }
}
