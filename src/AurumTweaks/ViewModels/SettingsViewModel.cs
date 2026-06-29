using System.Collections.Generic;
using AurumTweaks.Services;
using AurumTweaks.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AurumTweaks.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;
    private readonly IAppSettingsStore _store;

    [ObservableProperty] private string _selectedLanguage = "fr-FR";
    [ObservableProperty] private bool _createRestorePointBeforeTweaks = true;
    [ObservableProperty] private bool _strictCompetitiveAntiCheat;
    [ObservableProperty] private string _localAppDataPath = string.Empty;

    public IReadOnlyList<string> AvailableLanguages { get; } = new[] { "fr-FR", "en-US" };

    public SettingsViewModel(ILocalizationService localization, IAppSettingsStore store)
    {
        _localization = localization;
        _store = store;
        SelectedLanguage = _store.Current.Language;
        CreateRestorePointBeforeTweaks = _store.Current.CreateRestorePointBeforeTweaks;
        StrictCompetitiveAntiCheat = _store.Current.StrictCompetitiveAntiCheat;
        LocalAppDataPath = System.Environment.ExpandEnvironmentVariables("%LOCALAPPDATA%\\AurumTweaks");
        _localization.SetLanguage(SelectedLanguage);
    }

    partial void OnSelectedLanguageChanged(string value)
    {
        _localization.SetLanguage(value);
        _store.Current.Language = value;
        _ = _store.SaveAsync();
    }

    partial void OnCreateRestorePointBeforeTweaksChanged(bool value)
    {
        _store.Current.CreateRestorePointBeforeTweaks = value;
        _ = _store.SaveAsync();
    }

    partial void OnStrictCompetitiveAntiCheatChanged(bool value)
    {
        _store.Current.StrictCompetitiveAntiCheat = value;
        _ = _store.SaveAsync();
    }

    [RelayCommand]
    private void ShowWelcomeAgain()
    {
        var w = new WelcomeWindow();
        w.ShowDialog();
    }

    [RelayCommand]
    private void OpenLocalAppData() => ShellLauncher.OpenLocal(LocalAppDataPath);
}
