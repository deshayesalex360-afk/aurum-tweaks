using System.Collections.Generic;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Tests <see cref="LocalizationService"/> — every localized tweak name/description shown in the UI
/// flows through <see cref="LocalizationService.GetLocalizedFrom"/> (e.g. the dashboard's recommended
/// list), so its fallback chain is load-bearing: pick the current language, else French, else English,
/// else <i>something</i> — never a blank or a crash. We also pin the two no-crash guarantees:
/// an unknown resource key returns the key itself, and a malformed language code is swallowed.
///
/// Language is set explicitly via <see cref="LocalizationService.SetLanguage"/> in every
/// language-dependent test so results don't depend on the test host's OS culture.
/// </summary>
public class LocalizationServiceTests
{
    private static LocalizationService With(string lang)
    {
        var s = new LocalizationService();
        s.SetLanguage(lang);
        return s;
    }

    private static Dictionary<string, string> Map(params (string Key, string Value)[] pairs)
    {
        var d = new Dictionary<string, string>();
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    // ---- GetLocalizedFrom: the fallback chain ---------------------------------

    [Fact]
    public void GetLocalizedFrom_NullMap_ReturnsEmpty()
        => Assert.Equal(string.Empty, With("fr-FR").GetLocalizedFrom(null!));

    [Fact]
    public void GetLocalizedFrom_EmptyMap_ReturnsEmpty()
        => Assert.Equal(string.Empty, With("fr-FR").GetLocalizedFrom(new Dictionary<string, string>()));

    [Fact]
    public void GetLocalizedFrom_PrefersCurrentLanguage_French()
        => Assert.Equal("Bonjour",
            With("fr-FR").GetLocalizedFrom(Map(("fr", "Bonjour"), ("en", "Hello"))));

    [Fact]
    public void GetLocalizedFrom_PrefersCurrentLanguage_English_OverFrenchDefault()
        => Assert.Equal("Hello",
            With("en-US").GetLocalizedFrom(Map(("fr", "Bonjour"), ("en", "Hello"))));

    [Fact]
    public void GetLocalizedFrom_CurrentMissing_FallsBackToFrench()
        // English UI, but only a French string exists → show the French one rather than nothing.
        => Assert.Equal("Bonjour",
            With("en-US").GetLocalizedFrom(Map(("fr", "Bonjour"))));

    [Fact]
    public void GetLocalizedFrom_CurrentMissing_FallsBackToEnglish_WhenNoFrench()
        // French UI, only an English string exists → fall through fr to en.
        => Assert.Equal("Hello",
            With("fr-FR").GetLocalizedFrom(Map(("en", "Hello"))));

    [Fact]
    public void GetLocalizedFrom_UnsupportedUiLanguage_FallsBackToFrenchFirst()
        // A German UI (no de entry) gets the French default before the English one.
        => Assert.Equal("Bonjour",
            With("de-DE").GetLocalizedFrom(Map(("fr", "Bonjour"), ("en", "Hello"))));

    [Fact]
    public void GetLocalizedFrom_WhitespaceValue_IsSkipped_ForNextNonEmpty()
        // A present-but-blank French entry must not win over a real English one.
        => Assert.Equal("Hello",
            With("fr-FR").GetLocalizedFrom(Map(("fr", "   "), ("en", "Hello"))));

    [Fact]
    public void GetLocalizedFrom_NoLanguageMatch_ReturnsFirstEntry()
        // Neither current/fr/en present → last-resort: surface the only thing we have, not a blank.
        => Assert.Equal("Hallo",
            With("fr-FR").GetLocalizedFrom(Map(("de", "Hallo"))));

    // ---- Get: resource key lookup --------------------------------------------

    [Fact]
    public void Get_UnknownKey_ReturnsTheKeyItself_NeverThrows()
    {
        var svc = With("fr-FR");
        const string missing = "__definitely_not_a_real_resource_key__";
        Assert.Equal(missing, svc.Get(missing));
    }

    // ---- SetLanguage: valid + robustness -------------------------------------

    [Fact]
    public void SetLanguage_Valid_UpdatesCode_AndFiresEventOnce()
    {
        var svc = With("fr-FR");
        int fires = 0;
        svc.LanguageChanged += (_, _) => fires++;

        svc.SetLanguage("en-US");

        Assert.Equal("en-US", svc.CurrentLanguageCode);
        Assert.Equal(1, fires);
    }

    [Fact]
    public void SetLanguage_MalformedCode_IsSwallowed_LeavesLanguageUnchanged_NoEvent()
    {
        var svc = With("en-US");
        bool fired = false;
        svc.LanguageChanged += (_, _) => fired = true;

        // A 100-char subtag is an invalid culture name on every .NET runtime → CultureNotFoundException,
        // which the service must swallow rather than crash the app on a bad/persisted language code.
        var ex = Record.Exception(() => svc.SetLanguage(new string('z', 100)));

        Assert.Null(ex);
        Assert.Equal("en-US", svc.CurrentLanguageCode);
        Assert.False(fired);
    }
}
