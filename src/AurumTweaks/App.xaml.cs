using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using AurumTweaks.Services;
using AurumTweaks.Services.Interop;
using AurumTweaks.ViewModels;
using AurumTweaks.Views;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace AurumTweaks;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        var logsDir = Environment.ExpandEnvironmentVariables("%LOCALAPPDATA%\\AurumTweaks\\Logs");
        Directory.CreateDirectory(logsDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File(Path.Combine(logsDir, "aurum-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();

        Log.Information("Aurum Tweaks starting...");

        Services = ConfigureServices();
        AppDomain.CurrentDomain.UnhandledException += (s, a) =>
            Log.Fatal(a.ExceptionObject as Exception, "Unhandled exception");

        base.OnStartup(e);

        // Show splash with marble signature while the app boots its services.
        var splash = new SplashWindow();
        splash.Show();

        try
        {
            splash.SetStatus("Détection du hardware…");
            var hw = Services.GetRequiredService<IHardwareService>();
            _ = await hw.DetectAsync();

            splash.SetStatus("Chargement du catalogue de tweaks…");
            var repo = Services.GetRequiredService<ITweakRepository>();
            _ = await repo.LoadAllAsync();

            splash.SetStatus("Démarrage du monitoring…");
            Services.GetRequiredService<IMonitoringService>().Start();

            splash.SetStatus("Vérification de la licence…");
            await Services.GetRequiredService<ILicenseService>().InitialiseAsync();

            splash.SetStatus("Prêt.");
            await Task.Delay(400);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Splash boot failed");
        }

        // First-launch welcome
        var settingsStore = Services.GetRequiredService<IAppSettingsStore>();
        if (!settingsStore.Current.HasSeenWelcome)
        {
            splash.Hide();
            var welcome = new WelcomeWindow();
            welcome.ShowDialog();
            settingsStore.Current.HasSeenWelcome = true;
            settingsStore.Current.LastLaunchUtc = DateTime.UtcNow;
            await settingsStore.SaveAsync();
        }
        else
        {
            settingsStore.Current.LastLaunchUtc = DateTime.UtcNow;
            _ = settingsStore.SaveAsync();
        }

        var main = new MainWindow();
        main.Show();
        splash.Close();
        MainWindow = main;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Aurum Tweaks shutting down");
        try { Services.GetService<IMonitoringService>()?.Stop(); } catch { }
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Core services
        services.AddSingleton<ILocalizationService, LocalizationService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<INavigationHistory, NavigationHistory>();
        services.AddSingleton<IRegistryService, RegistryService>();
        services.AddSingleton<IServiceManagerService, ServiceManagerService>();
        services.AddSingleton<IRestorePointService, RestorePointService>();
        services.AddSingleton<IHardwareService, HardwareService>();
        services.AddSingleton<IMonitoringService, MonitoringService>();
        services.AddSingleton<ITweakRepository, TweakRepository>();
        services.AddSingleton<ITweakService, TweakService>();
        services.AddSingleton<IProfileService, ProfileService>();
        services.AddSingleton<IApplyJournal, ApplyJournal>();
        services.AddSingleton<IScoreHistoryStore, ScoreHistoryStore>();
        services.AddSingleton<ILicenseKeyRing, EmbeddedLicenseKeyRing>();
        services.AddSingleton<ILicenseStore, LicenseStore>();
        services.AddSingleton<ILicenseService, LicenseService>();
        services.AddSingleton<IGameDetectionService, GameDetectionService>();
        services.AddSingleton<IGameOcBindingStore, GameOcBindingStore>();
        services.AddSingleton<INetworkOptiService, NetworkOptiService>();
        services.AddSingleton<IAppSettingsStore, AppSettingsStore>();
        // Native-interop seams over the static NVAPI/ADLX wrappers, so GpuOcService's orchestration is
        // fakeable in tests and NVAPI access is serialized (see INvApi/IAdlxApi).
        services.AddSingleton<INvApi, NvApiBackend>();
        services.AddSingleton<IAdlxApi, AdlxBackend>();
        services.AddSingleton<IGpuOcService, GpuOcService>();
        // Integrated GPU stability test: real D3D11 compute load + Windows TDR (driver-reset) detection.
        services.AddSingleton<IGpuStressLoad, GpuStressLoad>();
        services.AddSingleton<IGpuTdrProbe, GpuTdrProbe>();
        services.AddSingleton<IGpuFanService, GpuFanService>();
        services.AddSingleton<IAdaptiveRecommendationService, AdaptiveRecommendationService>();
        services.AddSingleton<IBiosAdvisorService, BiosAdvisorService>();
        services.AddSingleton<IBiosApplyService, BiosApplyService>();
        services.AddSingleton<IDriverScanService, DriverScanService>();
        services.AddSingleton<IInputDeviceService, InputDeviceService>();
        services.AddSingleton<IStartupManagerService, StartupManagerService>();
        services.AddSingleton<IPowerPlanService, PowerPlanService>();
        services.AddSingleton<IScheduledTaskService, ScheduledTaskService>();
        services.AddSingleton<IAppxDebloatService, AppxDebloatService>();
        services.AddSingleton<IDiskCleanupService, DiskCleanupService>();
        services.AddSingleton<IDriveHealthService, DriveHealthService>();
        services.AddSingleton<IBenchmarkService, BenchmarkService>();
        services.AddSingleton<IBenchmarkHistoryService, BenchmarkHistoryService>();
        services.AddSingleton<IMemoryStabilityService, MemoryStabilityService>();
        services.AddSingleton<ICpuStabilityService, CpuStabilityService>();
        services.AddSingleton<IProcessControlService, ProcessControlService>();
        services.AddSingleton<ILatencyDiagnosticsService, LatencyDiagnosticsService>();
        services.AddSingleton<ITimerResolutionService, TimerResolutionService>();
        services.AddSingleton<IServiceControlService, ServiceControlService>();
        services.AddSingleton<IVisualEffectsService, VisualEffectsService>();
        services.AddSingleton<IMemoryManagementService, MemoryManagementService>();
        services.AddSingleton<IPrivacyService, PrivacyService>();
        services.AddSingleton<IGameOptiService, GameOptiService>();
        services.AddSingleton<IRestoreManagerService, RestoreManagerService>();
        services.AddSingleton<ISleepHibernationService, SleepHibernationService>();
        services.AddSingleton<IMemoryModulesService, MemoryModulesService>();
        services.AddSingleton<INetworkAdaptersService, NetworkAdaptersService>();
        services.AddSingleton<IPagefileService, PagefileService>();
        services.AddSingleton<IAudioService, AudioService>();
        services.AddSingleton<IWindowsUpdateService, WindowsUpdateService>();
        services.AddSingleton<IDisplayService, DisplayService>();
        services.AddSingleton<IDnsService, DnsService>();
        services.AddSingleton<ISnapshotService, SnapshotService>();
        services.AddSingleton<IPendingRebootService, PendingRebootService>();
        services.AddSingleton<IPreflightService, PreflightService>();
        services.AddSingleton<IEvidenceStore, EvidenceStore>();
        // Inject the durable store so the frame-time A/B survives a restart; the settings diff and live score stay
        // in-memory by design (see IEvidenceStore). The factory makes that one dependency explicit.
        services.AddSingleton<IEvidenceLedger>(sp => new EvidenceLedger(sp.GetRequiredService<IEvidenceStore>()));

        // ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<CommandPaletteViewModel>();
        // Shared child VM: one pre-flight safety banner, injected into every apply surface (Tweaks, Dashboard) so they
        // show one identical, machine-global safety-net posture from a single probe.
        services.AddSingleton<PreflightBannerViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<TweaksViewModel>();
        services.AddSingleton<BiosViewModel>();
        services.AddSingleton<OverclockingViewModel>();
        services.AddSingleton<GamingViewModel>();
        services.AddSingleton<StartupViewModel>();
        services.AddSingleton<PowerPlanViewModel>();
        services.AddSingleton<ScheduledTasksViewModel>();
        services.AddSingleton<AppxDebloatViewModel>();
        services.AddSingleton<DiskCleanupViewModel>();
        services.AddSingleton<DriveHealthViewModel>();
        services.AddSingleton<DriversViewModel>();
        services.AddSingleton<DevicesViewModel>();
        services.AddSingleton<MonitoringViewModel>();
        services.AddSingleton<ProfilesViewModel>();
        services.AddSingleton<JournalViewModel>();
        services.AddSingleton<TipsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<TransparencyViewModel>();
        services.AddSingleton<LicenseViewModel>();
        services.AddSingleton<RamCalculatorViewModel>();
        services.AddSingleton<BenchmarkViewModel>();
        services.AddSingleton<StabilityViewModel>();
        services.AddSingleton<CpuStabilityViewModel>();
        services.AddSingleton<ProcessControlViewModel>();
        services.AddSingleton<LatencyDiagnosticsViewModel>();
        services.AddSingleton<ServiceControlViewModel>();
        services.AddSingleton<VisualEffectsViewModel>();
        services.AddSingleton<MemoryViewModel>();
        services.AddSingleton<PrivacyViewModel>();
        services.AddSingleton<GameOptiViewModel>();
        services.AddSingleton<RestoreManagerViewModel>();
        services.AddSingleton<SleepHibernationViewModel>();
        services.AddSingleton<MemoryModulesViewModel>();
        services.AddSingleton<NetworkAdaptersViewModel>();
        services.AddSingleton<PagefileViewModel>();
        services.AddSingleton<AudioViewModel>();
        services.AddSingleton<WindowsUpdateViewModel>();
        services.AddSingleton<DisplayViewModel>();
        services.AddSingleton<DnsViewModel>();
        services.AddSingleton<SnapshotViewModel>();

        return services.BuildServiceProvider();
    }
}
