using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace AurumTweaks.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableObject? _currentViewModel;

    [ObservableProperty]
    private string _currentPageLabel = "Tableau de bord";

    /// <summary>The Ctrl+K command palette, exposed so the window shell can bind its overlay. Owned here (one-way)
    /// so the palette never has to know about the page graph — it just emits an intent we resolve in Navigate.</summary>
    public CommandPaletteViewModel Palette { get; }

    private readonly INavigationHistory _history;

    public MainViewModel(CommandPaletteViewModel palette, INavigationHistory history)
    {
        Palette = palette;
        _history = history;
        Palette.NavigationRequested += OnPaletteNavigationRequested;
        Navigate("Dashboard");
    }

    // Resolve a palette pick into an actual effect. A page row navigates straight to its key; a tweak row deep-links
    // into the Tweaks page pre-filtered to that one tweak (clearing any prior tier/category/AC filter first, so
    // "jump to this tweak" always reveals it instead of landing on a list that silently filtered it out); an action
    // row runs a global command via RunPaletteAction.
    private void OnPaletteNavigationRequested(object? sender, PaletteEntry entry)
    {
        switch (entry.Kind)
        {
            case PaletteEntryKind.Tweak:
            {
                var tweaks = App.Services.GetRequiredService<TweaksViewModel>();
                tweaks.SelectedTier = null;
                tweaks.SelectedCategory = null;
                tweaks.CompetitiveSafeOnly = false;
                tweaks.SearchText = entry.Title;
                Navigate("Tweaks");
                break;
            }
            case PaletteEntryKind.Action:
                RunPaletteAction(entry.Id);
                break;
            default:
                Navigate(entry.Id);
                break;
        }
    }

    // Run a global palette action: jump to the page that owns it (so its status/feedback is visible) and invoke its
    // command — the same App.Services reach used for the tweak deep-link above. The report export/copy commands are
    // honest no-ops when their data isn't ready (e.g. hardware still detecting), so firing them here is always safe;
    // the journal export awaits a fresh reload first so a just-applied batch can't be missing from the exported file.
    // Every id here must have a matching action in PaletteActionCatalog — a unit test pins the two in sync.
    private async void RunPaletteAction(string actionId)
    {
        switch (actionId)
        {
            case "ExportSystemReport":
                Navigate("Dashboard");
                App.Services.GetRequiredService<DashboardViewModel>().ExportSystemReportCommand.Execute(null);
                break;
            case "CopySystemReport":
                Navigate("Dashboard");
                App.Services.GetRequiredService<DashboardViewModel>().CopySystemReportCommand.Execute(null);
                break;
            case "ExportJournal":
            {
                Navigate("Journal");
                var journal = App.Services.GetRequiredService<JournalViewModel>();
                await journal.RefreshAsync();
                journal.ExportCommand.Execute(null);
                break;
            }
            case "CopyJournal":
            {
                Navigate("Journal");
                var journal = App.Services.GetRequiredService<JournalViewModel>();
                await journal.RefreshAsync();
                journal.CopyCommand.Execute(null);
                break;
            }
            case "ExportEvidenceReport":
                Navigate("Dashboard");
                App.Services.GetRequiredService<DashboardViewModel>().ExportEvidenceReportCommand.Execute(null);
                break;
            case "CopyEvidenceReport":
                Navigate("Dashboard");
                App.Services.GetRequiredService<DashboardViewModel>().CopyEvidenceReportCommand.Execute(null);
                break;
            case "ExportTransparency":
                Navigate("Transparency");
                App.Services.GetRequiredService<TransparencyViewModel>().ExportReportCommand.Execute(null);
                break;
            case "CopyTransparency":
                Navigate("Transparency");
                App.Services.GetRequiredService<TransparencyViewModel>().CopyReportCommand.Execute(null);
                break;
        }
    }

    public void Navigate(string pageKey)
    {
        (CurrentViewModel, CurrentPageLabel) = pageKey switch
        {
            "Dashboard"     => ((ObservableObject)App.Services.GetRequiredService<DashboardViewModel>(),   "Tableau de bord"),
            "Tweaks"        => (App.Services.GetRequiredService<TweaksViewModel>(),                        "Tweaks Windows"),
            "Bios"          => (App.Services.GetRequiredService<BiosViewModel>(),                          "BIOS · gros gains"),
            "RamCalc"       => (App.Services.GetRequiredService<RamCalculatorViewModel>(),                 "Calculatrice timings RAM"),
            "Stability"     => (App.Services.GetRequiredService<StabilityViewModel>(),                     "Stabilité mémoire"),
            "CpuStability"  => (App.Services.GetRequiredService<CpuStabilityViewModel>(),                  "Stabilité CPU"),
            "Overclocking"  => (App.Services.GetRequiredService<OverclockingViewModel>(),                  "Overclocking GPU"),
            "Gaming"        => (App.Services.GetRequiredService<GamingViewModel>(),                        "Gaming"),
            "Startup"       => (App.Services.GetRequiredService<StartupViewModel>(),                       "Démarrage"),
            "Power"         => (App.Services.GetRequiredService<PowerPlanViewModel>(),                     "Alimentation"),
            "ScheduledTasks"=> (App.Services.GetRequiredService<ScheduledTasksViewModel>(),                "Tâches planifiées"),
            "Appx"          => (App.Services.GetRequiredService<AppxDebloatViewModel>(),                    "Applications préinstallées"),
            "DiskCleanup"   => (App.Services.GetRequiredService<DiskCleanupViewModel>(),                    "Nettoyage disque"),
            "DriveHealth"   => (App.Services.GetRequiredService<DriveHealthViewModel>(),                    "Santé des disques"),
            "ProcessControl"=> (App.Services.GetRequiredService<ProcessControlViewModel>(),                 "Priorité & affinité"),
            "Latency"       => (App.Services.GetRequiredService<LatencyDiagnosticsViewModel>(),             "Latence système (DPC/ISR)"),
            "Services"      => (App.Services.GetRequiredService<ServiceControlViewModel>(),                "Services Windows"),
            "VisualEffects" => (App.Services.GetRequiredService<VisualEffectsViewModel>(),                "Effets visuels"),
            "Memory"        => (App.Services.GetRequiredService<MemoryViewModel>(),                       "Mémoire vive"),
            "Privacy"       => (App.Services.GetRequiredService<PrivacyViewModel>(),                      "Confidentialité"),
            "GameOpti"      => (App.Services.GetRequiredService<GameOptiViewModel>(),                     "Optimisations jeu"),
            "RestorePoints" => (App.Services.GetRequiredService<RestoreManagerViewModel>(),               "Points de restauration"),
            "SleepHib"      => (App.Services.GetRequiredService<SleepHibernationViewModel>(),             "Veille & hibernation"),
            "MemoryModules" => (App.Services.GetRequiredService<MemoryModulesViewModel>(),               "Barrettes mémoire"),
            "NetworkAdapters"=> (App.Services.GetRequiredService<NetworkAdaptersViewModel>(),              "Cartes réseau"),
            "Pagefile"      => (App.Services.GetRequiredService<PagefileViewModel>(),                     "Mémoire virtuelle"),
            "Audio"         => (App.Services.GetRequiredService<AudioViewModel>(),                        "Son"),
            "WindowsUpdate" => (App.Services.GetRequiredService<WindowsUpdateViewModel>(),                "Windows Update"),
            "Display"       => (App.Services.GetRequiredService<DisplayViewModel>(),                     "Affichage · fréquence"),
            "Dns"           => (App.Services.GetRequiredService<DnsViewModel>(),                         "Serveurs DNS"),
            "Benchmark"     => (App.Services.GetRequiredService<BenchmarkViewModel>(),                     "Benchmark · frame-times"),
            "Drivers"       => (App.Services.GetRequiredService<DriversViewModel>(),                       "Pilotes"),
            "Devices"       => (App.Services.GetRequiredService<DevicesViewModel>(),                       "Périphériques · input"),
            "Monitoring"    => (App.Services.GetRequiredService<MonitoringViewModel>(),                    "Monitoring temps réel"),
            "Profiles"      => (App.Services.GetRequiredService<ProfilesViewModel>(),                      "Profils"),
            "Journal"       => (App.Services.GetRequiredService<JournalViewModel>(),                       "Journal des modifications"),
            "Snapshots"     => (App.Services.GetRequiredService<SnapshotViewModel>(),                      "Instantanés système"),
            "Tips"          => (App.Services.GetRequiredService<TipsViewModel>(),                          "Conseils & recommandations"),
            "Settings"      => (App.Services.GetRequiredService<SettingsViewModel>(),                      "Paramètres"),
            "Transparency"  => (App.Services.GetRequiredService<TransparencyViewModel>(),                  "Transparence & confiance"),
            "License"       => (App.Services.GetRequiredService<LicenseViewModel>(),                       "Licence"),
            _ => (CurrentViewModel, CurrentPageLabel)
        };

        // Feed the session trail behind the palette's "recent first" ordering. Recording an unmatched key is
        // harmless: PaletteRecency only ever promotes rows that correspond to a real page, so a key with no page
        // never surfaces — but in practice every caller passes a catalog key, so this is the genuine visit log.
        _history.Record(pageKey);
    }
}
