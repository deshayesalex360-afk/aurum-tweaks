using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="StartupClassifier"/> — the heuristic that buckets a startup program and words the
/// "safe to disable?" guidance on the Démarrage page. The honesty rules pinned here: a driver/security helper is
/// the one bucket we refuse to paint as safe-to-disable (and its signature must win over the generic "update"
/// rule), an unrecognised program is honestly <see cref="StartupCategory.Other"/> (still "safe" — the user
/// decides), and every category yields a non-empty French label and advice.
/// </summary>
public class StartupClassifierTests
{
    [Theory]
    [InlineData("Steam", "C:\\Program Files (x86)\\Steam\\steam.exe", StartupCategory.Launcher)]
    [InlineData("EpicGamesLauncher", "D:\\Epic\\EpicGamesLauncher.exe", StartupCategory.Launcher)]
    [InlineData("Battle.net", "C:\\Games\\Battle.net.exe", StartupCategory.Launcher)]
    [InlineData("EADesktop", "C:\\EA\\EADesktop.exe", StartupCategory.Launcher)]
    [InlineData("Discord", "C:\\Users\\x\\Discord\\Discord.exe", StartupCategory.Communication)]
    [InlineData("Microsoft Teams", "C:\\Teams\\Teams.exe", StartupCategory.Communication)]
    [InlineData("OneDrive", "C:\\OneDrive\\OneDrive.exe", StartupCategory.Cloud)]
    [InlineData("Dropbox", "C:\\Dropbox\\Dropbox.exe", StartupCategory.Cloud)]
    [InlineData("Spotify", "C:\\Spotify\\Spotify.exe", StartupCategory.Media)]
    [InlineData("iCUE", "C:\\Corsair\\iCUE.exe", StartupCategory.Peripheral)]
    [InlineData("Razer Synapse", "C:\\Razer\\Razer Synapse 3.exe", StartupCategory.Peripheral)]
    [InlineData("GoogleUpdate", "C:\\Google\\Update\\GoogleUpdate.exe", StartupCategory.Updater)]
    [InlineData("Adobe Updater", "C:\\Adobe\\AdobeARM.exe", StartupCategory.Updater)]
    public void Classify_KnownPrograms_LandInExpectedCategory(string name, string exe, StartupCategory expected)
        => Assert.Equal(expected, StartupClassifier.Classify(name, exe));

    [Theory]
    [InlineData("NVIDIA App", "C:\\NVIDIA\\nvcontainer.exe")]
    [InlineData("Realtek HD Audio", "C:\\Realtek\\RtkAudUService64.exe")]
    [InlineData("Windows Security", "C:\\Windows\\SecurityHealthSystray.exe")]
    public void Classify_DriversAndSecurity_AreSecurityOrDriver(string name, string exe)
        => Assert.Equal(StartupCategory.SecurityOrDriver, StartupClassifier.Classify(name, exe));

    [Fact]
    public void Classify_UnknownProgram_IsOther()
        => Assert.Equal(StartupCategory.Other, StartupClassifier.Classify("Acme Widget", "C:\\acme\\widget.exe"));

    [Fact]
    public void Classify_EmptyInput_IsOther()
        => Assert.Equal(StartupCategory.Other, StartupClassifier.Classify("", ""));

    [Fact]
    public void Classify_MatchesOnExecutablePath_WhenNameIsGeneric()
        => Assert.Equal(StartupCategory.Cloud, StartupClassifier.Classify("Sync", "C:\\Program Files\\OneDrive\\OneDrive.exe"));

    [Fact]
    public void Classify_DriverSignatureWins_OverGenericUpdateToken()
        // A driver bundled with an "Update" helper must stay SecurityOrDriver — the generic "update"→Updater rule
        // sits below the driver signatures on purpose, so a GPU/audio helper is never mislabelled "safe to disable".
        => Assert.Equal(StartupCategory.SecurityOrDriver, StartupClassifier.Classify("NVIDIA Update", "C:\\NVIDIA\\nvcontainer.exe"));

    [Theory]
    [InlineData(StartupCategory.SecurityOrDriver, false)]
    [InlineData(StartupCategory.Launcher, true)]
    [InlineData(StartupCategory.Updater, true)]
    [InlineData(StartupCategory.Cloud, true)]
    [InlineData(StartupCategory.Peripheral, true)]
    [InlineData(StartupCategory.Other, true)]
    public void IsSafeToDisable_OnlyDriversAndSecurityAreUnsafe(StartupCategory category, bool expected)
        => Assert.Equal(expected, StartupClassifier.IsSafeToDisable(category));

    [Theory]
    [InlineData(StartupCategory.Launcher)]
    [InlineData(StartupCategory.Communication)]
    [InlineData(StartupCategory.Cloud)]
    [InlineData(StartupCategory.Media)]
    [InlineData(StartupCategory.Updater)]
    [InlineData(StartupCategory.Peripheral)]
    [InlineData(StartupCategory.SecurityOrDriver)]
    [InlineData(StartupCategory.Other)]
    public void DescribeAndAdvice_AreNonEmpty_ForEveryCategory(StartupCategory category)
    {
        Assert.False(string.IsNullOrWhiteSpace(StartupClassifier.Describe(category)));
        Assert.False(string.IsNullOrWhiteSpace(StartupClassifier.Advice(category)));
    }
}
