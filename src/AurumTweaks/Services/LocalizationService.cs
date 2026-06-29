using System;
using System.Collections.Generic;
using System.Globalization;
using System.Resources;
using System.Threading;

namespace AurumTweaks.Services;

public sealed class LocalizationService : ILocalizationService
{
    private static readonly ResourceManager Rm = new(
        "AurumTweaks.Localization.Strings",
        typeof(LocalizationService).Assembly);

    private CultureInfo _current = new("fr-FR");

    public string CurrentLanguageCode => _current.Name;

    public event EventHandler? LanguageChanged;

    public LocalizationService()
    {
        // Pick best match from OS, fall back to French.
        var os = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        _current = os == "en" ? new CultureInfo("en-US") : new CultureInfo("fr-FR");
        Thread.CurrentThread.CurrentUICulture = _current;
    }

    public string Get(string key)
    {
        try
        {
            return Rm.GetString(key, _current) ?? key;
        }
        catch
        {
            return key;
        }
    }

    public string GetLocalizedFrom(IDictionary<string, string> map)
    {
        if (map is null || map.Count == 0) return string.Empty;
        var lang = _current.TwoLetterISOLanguageName;
        if (map.TryGetValue(lang, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        if (map.TryGetValue("fr", out var fr) && !string.IsNullOrWhiteSpace(fr)) return fr;
        if (map.TryGetValue("en", out var en) && !string.IsNullOrWhiteSpace(en)) return en;
        foreach (var kvp in map) return kvp.Value;
        return string.Empty;
    }

    public void SetLanguage(string langCode)
    {
        try
        {
            _current = new CultureInfo(langCode);
            Thread.CurrentThread.CurrentUICulture = _current;
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (CultureNotFoundException) { /* ignore */ }
    }
}
