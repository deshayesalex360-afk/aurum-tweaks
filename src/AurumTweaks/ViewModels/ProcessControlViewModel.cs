using System.Collections.ObjectModel;
using System.Threading.Tasks;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AurumTweaks.ViewModels;

/// <summary>
/// « Priorité &amp; affinité CPU » page — list the running processes worth tuning and let the user raise a game's
/// priority or pin it to the performance cores. Every action is real: after each change the page RE-READS the live
/// state from Windows, so a refused write shows the unchanged priority/affinity rather than a fabricated success.
/// It never claims FPS gains (only fewer scheduler-induced hitches). Persistence is opt-in through a visible scheduled
/// task; no hidden driver/service is implied.
/// </summary>
public partial class ProcessControlViewModel : ObservableObject
{
    private readonly IProcessControlService _service;

    public ObservableCollection<RunningProcessInfo> Processes { get; } = new();
    public ObservableCollection<PersistentProcessRule> PersistentRules { get; } = new();

    [ObservableProperty] private string _headline = "Lecture des processus…";
    [ObservableProperty] private string _cpuSummary = string.Empty;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private string _persistenceStatus = "Règles persistantes non lues.";
    [ObservableProperty] private string _persistenceLimit = ProcessPersistencePlan.UiLimit;
    [ObservableProperty] private bool _canInstallPersistenceTask;
    [ObservableProperty] private bool _canRemovePersistenceTask;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _queryFailed;

    public ProcessControlViewModel(IProcessControlService service)
    {
        _service = service;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        await ReloadAsync();
        IsBusy = false;
    }

    /// <summary>Open Windows' own Task Manager — the honest complement that can also set priority/affinity and persists nothing either.</summary>
    [RelayCommand]
    private void OpenTaskManager() =>
        ShellLauncher.OpenLocal(System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.System), "Taskmgr.exe"));

    [RelayCommand] private Task SetPriorityNormal(RunningProcessInfo? p) => ApplyPriority(p, ProcessPriorityLevel.Normal);
    [RelayCommand] private Task SetPriorityAbove(RunningProcessInfo? p)  => ApplyPriority(p, ProcessPriorityLevel.AboveNormal);
    [RelayCommand] private Task SetPriorityHigh(RunningProcessInfo? p)   => ApplyPriority(p, ProcessPriorityLevel.High);

    [RelayCommand] private Task SetAffinityAll(RunningProcessInfo? p)         => ApplyAffinity(p, AffinityStrategy.AllCores);
    [RelayCommand] private Task SetAffinityPerformance(RunningProcessInfo? p) => ApplyAffinity(p, AffinityStrategy.PerformanceCores);
    [RelayCommand] private Task Persist(RunningProcessInfo? p) => AddPersistentRule(p, includeHighPerformancePowerPlan: false);
    [RelayCommand] private Task PersistWithPowerPlan(RunningProcessInfo? p) => AddPersistentRule(p, includeHighPerformancePowerPlan: true);

    /// <summary>Apply the recommended priority+affinity in one click (the per-process advice the row already shows).</summary>
    [RelayCommand]
    private async Task Optimize(RunningProcessInfo? p)
    {
        if (p is null || IsBusy) return;
        IsBusy = true;
        var okP = await _service.SetPriorityAsync(p.Pid, p.Advice.RecommendedPriority);
        var okA = await _service.SetAffinityAsync(p.Pid, p.Advice.RecommendedAffinity);
        await ReloadAsync();
        Status = okP && okA
            ? $"{p.DisplayName} optimisé (priorité {PriorityLevels.Label(p.Advice.RecommendedPriority).ToLowerInvariant()}, affinité ajustée)."
            : "Windows a refusé tout ou partie de la modification — l'état réel est affiché ci-dessus.";
        IsBusy = false;
    }

    private async Task ApplyPriority(RunningProcessInfo? p, ProcessPriorityLevel level)
    {
        if (p is null || IsBusy) return;
        IsBusy = true;
        var ok = await _service.SetPriorityAsync(p.Pid, level);
        await ReloadAsync();
        Status = ok
            ? $"Priorité de {p.DisplayName} réglée sur « {PriorityLevels.Label(level).ToLowerInvariant()} »."
            : "Windows a refusé la modification de priorité — l'état réel est affiché ci-dessus.";
        IsBusy = false;
    }

    private async Task ApplyAffinity(RunningProcessInfo? p, AffinityStrategy strategy)
    {
        if (p is null || IsBusy) return;
        IsBusy = true;
        var ok = await _service.SetAffinityAsync(p.Pid, strategy);
        await ReloadAsync();
        Status = ok
            ? $"Affinité de {p.DisplayName} mise à jour."
            : "Windows a refusé la modification d'affinité — l'état réel est affiché ci-dessus.";
        IsBusy = false;
    }

    [RelayCommand]
    private async Task InstallPersistenceTaskAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        var ok = await _service.SetPersistenceTaskEnabledAsync(enabled: true);
        await ReloadAsync();
        Status = ok
            ? "Tâche planifiée Aurum installée. Elle est visible dans le Planificateur de tâches Windows."
            : "Impossible d'installer la tâche planifiée Aurum ; aucune règle n'a été appliquée en arrière-plan.";
        IsBusy = false;
    }

    [RelayCommand]
    private async Task RemovePersistenceTaskAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        var ok = await _service.SetPersistenceTaskEnabledAsync(enabled: false);
        await ReloadAsync();
        Status = ok
            ? "Tâche planifiée Aurum retirée. Les règles locales restent consultables."
            : "Impossible de retirer la tâche planifiée Aurum ; vérifie le Planificateur de tâches Windows.";
        IsBusy = false;
    }

    [RelayCommand]
    private async Task RemovePersistentRuleAsync(PersistentProcessRule? rule)
    {
        if (rule is null || IsBusy) return;
        IsBusy = true;
        var ok = await _service.RemovePersistentRuleAsync(rule.ProcessName);
        await ReloadAsync();
        Status = ok
            ? $"Règle persistante retirée pour {rule.DisplayName}."
            : "Impossible de retirer cette règle persistante.";
        IsBusy = false;
    }

    private async Task ReloadAsync()
    {
        Status = "Lecture des processus…";
        var reportTask = _service.GetReportAsync();
        var persistenceTask = _service.GetPersistenceReportAsync();
        await Task.WhenAll(reportTask, persistenceTask);
        var report = reportTask.Result;
        var persistence = persistenceTask.Result;

        Processes.Clear();
        foreach (var proc in report.Processes) Processes.Add(proc);

        PersistentRules.Clear();
        foreach (var rule in persistence.Rules) PersistentRules.Add(rule);

        QueryFailed = !report.QueryOk;
        CpuSummary = report.CpuSummary;
        Headline = report.QueryOk
            ? $"{report.GameCount} jeu(x) en cours · {report.Count} processus listé(s)"
            : "Impossible de lister les processus";
        Status = report.QueryOk
            ? "Astuce : « Optimiser » applique maintenant. « Persister » enregistre une règle locale, active seulement si tu installes la tâche planifiée."
            : "Aucun processus listé — accès refusé ou énumération impossible.";
        PersistenceStatus = persistence.StateDisplay;
        CanInstallPersistenceTask = persistence.Count > 0 && !persistence.TaskInstalled;
        CanRemovePersistenceTask = persistence.TaskInstalled;
    }

    private async Task AddPersistentRule(RunningProcessInfo? p, bool includeHighPerformancePowerPlan)
    {
        if (p is null || IsBusy) return;
        IsBusy = true;
        var ok = await _service.AddPersistentRuleAsync(p, includeHighPerformancePowerPlan);
        await ReloadAsync();
        Status = ok
            ? includeHighPerformancePowerPlan
                ? $"Règle persistante enregistrée pour {p.DisplayName}, avec plan Performances élevées pendant l'exécution."
                : $"Règle persistante enregistrée pour {p.DisplayName}."
            : "Impossible d'enregistrer la règle persistante (processus inaccessible ou plan actif illisible).";
        IsBusy = false;
    }
}
