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
/// It never claims FPS gains (only fewer scheduler-induced hitches) and discloses that a change is not persistent.
/// </summary>
public partial class ProcessControlViewModel : ObservableObject
{
    private readonly IProcessControlService _service;

    public ObservableCollection<RunningProcessInfo> Processes { get; } = new();

    [ObservableProperty] private string _headline = "Lecture des processus…";
    [ObservableProperty] private string _cpuSummary = string.Empty;
    [ObservableProperty] private string? _status;
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

    private async Task ReloadAsync()
    {
        Status = "Lecture des processus…";
        var report = await _service.GetReportAsync();

        Processes.Clear();
        foreach (var proc in report.Processes) Processes.Add(proc);

        QueryFailed = !report.QueryOk;
        CpuSummary = report.CpuSummary;
        Headline = report.QueryOk
            ? $"{report.GameCount} jeu(x) en cours · {report.Count} processus listé(s)"
            : "Impossible de lister les processus";
        Status = report.QueryOk
            ? "Astuce : « Optimiser » applique la recommandation. Une modification s'applique au processus en cours — au prochain lancement, ré-applique-la."
            : "Aucun processus listé — accès refusé ou énumération impossible.";
    }
}
