using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace AurumTweaks.ViewModels;

/// <summary>
/// One preset offered for one adapter — a thin view projection over the pure <see cref="DnsAdapterState"/> gating
/// so the apply button can bind both the "would this change anything" guard and the parameters it needs.
/// </summary>
public sealed record DnsPresetChoice(DnsAdapterState Adapter, DnsPreset Preset)
{
    public string Name => Preset.Name;
    public string ServersDisplay => Preset.ServersDisplay;
    public string Description => Preset.Description;

    /// <summary>True when this preset is already the adapter's live static config — drives the « Actif » badge.</summary>
    public bool IsActive => Adapter.IsUsingPreset(Preset);

    /// <summary>The apply button is enabled only when applying would genuinely change the static DNS config.</summary>
    public bool CanApply => Adapter.CanApply(Preset);
}

/// <summary>One adapter row: its live state plus the per-adapter list of preset choices shown beneath it.</summary>
public sealed class DnsAdapterRow
{
    public DnsAdapterState State { get; }
    public IReadOnlyList<DnsPresetChoice> Presets { get; }

    public DnsAdapterRow(DnsAdapterState state)
    {
        State = state;
        Presets = DnsPresetCatalog.Presets.Select(p => new DnsPresetChoice(state, p)).ToList();
    }
}

/// <summary>
/// « Serveurs DNS » — reads each adapter's DNS state and applies a curated resolver (or reverts to DHCP) in-app, and
/// closes the measure→apply loop ON THIS PAGE: « Comparer et appliquer le plus rapide » runs the real resolver
/// benchmark, then offers one click to write the winner onto the active adapter. The benchmark winner is bridged to
/// an applicable preset BY ADDRESS (the benchmark and catalog name the same providers differently), and every honesty
/// path — nobody answered, your own DNS won, the winner has no preset, no active adapter, already applied — is decided
/// in the pure <see cref="DnsRecommendation"/>, so the apply button is offered only when it would genuinely change the
/// config. The top-bar Refresh + benchmark gate via CanExecute; the per-row and recommended apply buttons self-gate via
/// IsEnabled, with an IsBusy re-entrancy guard inside <see cref="ApplyAsync"/>. Every change is reflected by re-reading
/// the live state, so a refused write surfaces the measured config rather than a fake success.
/// </summary>
public partial class DnsViewModel : ObservableObject
{
    private readonly IDnsService _dns;
    private readonly INetworkOptiService _network;

    /// <summary>The last computed recommendation, held so « Appliquer » knows exactly which preset/adapter to write.</summary>
    private DnsRecommendation? _pendingRecommendation;

    public ObservableCollection<DnsAdapterRow> Adapters { get; } = new();

    [ObservableProperty] private string _headline = "Analyse des cartes réseau…";
    [ObservableProperty] private bool _hasAdapters;
    [ObservableProperty] private string? _status;

    /// <summary>The benchmark verdict shown above the adapter list; null hides the panel.</summary>
    [ObservableProperty] private string? _recommendation;

    /// <summary>True only when the verdict maps to a real, applicable change — drives the « Appliquer » button.</summary>
    [ObservableProperty] private bool _canApplyRecommended;

    /// <summary>
    /// The last benchmark's full ranked table — surfaced on-page as the EVIDENCE behind the recommendation, and the
    /// source the shareable report renders. Null hides the table and self-disables copy/export, so neither ever acts on
    /// a stale or empty run.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBenchmark))]
    [NotifyCanExecuteChangedFor(nameof(CopyReportCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportReportCommand))]
    private DnsBenchmarkReport? _lastBenchmark;

    public bool HasBenchmark => LastBenchmark is { Ranked.Count: > 0 };

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(BenchmarkFastestCommand))]
    private bool _isBusy;

    public DnsViewModel(IDnsService dns, INetworkOptiService network)
    {
        _dns = dns;
        _network = network;
        _ = LoadAsync();
    }

    private bool CanRefresh() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try { await LoadAsync(); }
        finally { IsBusy = false; }
    }

    private async Task LoadAsync()
    {
        var report = await _dns.GetReportAsync();
        Adapters.Clear();
        foreach (var a in report.Adapters)
            Adapters.Add(new DnsAdapterRow(a));
        HasAdapters = report.Any;
        Headline = report.Headline;
    }

    [RelayCommand]
    private Task ApplyPreset(DnsPresetChoice? choice) =>
        choice is { CanApply: true } c
            ? ApplyAsync(() => _dns.ApplyPresetAsync(c.Adapter.SettingId, c.Adapter.Name, c.Preset))
            : Task.CompletedTask;

    [RelayCommand]
    private Task Revert(DnsAdapterState? adapter) =>
        adapter is { CanRevertToAutomatic: true } a
            ? ApplyAsync(() => _dns.RevertToAutomaticAsync(a.SettingId, a.Name))
            : Task.CompletedTask;

    private async Task ApplyAsync(Func<Task<DnsApplyOutcome>> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var outcome = await action();
            await LoadAsync();              // reflect the MEASURED state before showing the verdict
            Status = outcome.Summary;
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Run the real resolver benchmark and turn its winner into an honest, applicable verdict against the active
    /// adapter. All the honesty (no responders, your own DNS won, no curated preset, no active adapter, already
    /// applied) is decided by <see cref="DnsRecommendation.From"/> — this only runs the probe and surfaces the result.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task BenchmarkFastestAsync()
    {
        IsBusy = true;
        Recommendation = "Mesure des résolveurs DNS en cours…";
        CanApplyRecommended = false;
        LastBenchmark = null;        // drop the previous ranking so a stale table never lingers during the run
        try
        {
            var report = await _network.BenchmarkDnsAsync();
            LastBenchmark = report;  // surface the measured ranking as evidence + enable copy/export
            var active = Adapters.Select(r => r.State).FirstOrDefault(s => s.IsConnected);
            var rec = DnsRecommendation.From(report, active);
            _pendingRecommendation = rec;
            Recommendation = rec.Message;
            CanApplyRecommended = rec.CanApply;
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Write the recommended preset onto the recommended adapter. Guarded on the stored verdict's own
    /// <see cref="DnsRecommendation.CanApply"/> (so it can never act on a no-op verdict), it goes through the shared
    /// <see cref="ApplyAsync"/> — which re-reads the live config — then clears the panel since the authoritative
    /// outcome now lives in <see cref="Status"/>.
    /// </summary>
    [RelayCommand]
    private async Task ApplyRecommendedAsync()
    {
        var rec = _pendingRecommendation;
        if (rec is not { CanApply: true }) return;
        var preset = rec.Preset;
        var adapter = rec.Adapter;
        if (preset is null || adapter is null) return;   // invariant: CanApply ⇒ both set; satisfies nullable analysis

        await ApplyAsync(() => _dns.ApplyPresetAsync(adapter.SettingId, adapter.Name, preset));

        _pendingRecommendation = null;
        Recommendation = null;
        CanApplyRecommended = false;
    }

    [RelayCommand]
    private void OpenNetworkConnections() => ShellLauncher.OpenLocal("ncpa.cpl");

    [RelayCommand]
    private void OpenNetworkSettings() => ShellLauncher.OpenLink("ms-settings:network-status");

    /// <summary>Jump to the Gaming page where the real DNS benchmark lives — find the fastest, then apply it here.</summary>
    [RelayCommand]
    private void GoToBenchmark() => App.Services.GetRequiredService<MainViewModel>().Navigate("Gaming");

    /// <summary>
    /// Copy the last benchmark as a shareable text report — the ranked resolvers with their median latency, the winner,
    /// and the load-bearing « un DNS plus rapide ≠ moins de ping en jeu » caveat. The render is the tested pure
    /// <see cref="DnsBenchmarkTextReport"/>; thin clipboard glue, self-disabled until a real benchmark exists.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasBenchmark))]
    private void CopyReport()
    {
        if (LastBenchmark is not { Ranked.Count: > 0 } report) return;
        var text = DnsBenchmarkTextReport.Render(report, DateTime.UtcNow);
        try
        {
            System.Windows.Clipboard.SetText(text);
            Status = "Benchmark DNS copié — colle-le où tu veux (forum, Discord, thread OC).";
        }
        catch
        {
            // The clipboard can be momentarily locked by another process — non-fatal, point at the file route.
            Status = "Copie impossible (presse-papiers occupé). Utilise « Exporter » à la place.";
        }
    }

    /// <summary>Save the same benchmark as a .txt the user can archive. Same tested render as the copy path; this is the
    /// save-dialog + file-write glue, and it honestly reports a write failure.</summary>
    [RelayCommand(CanExecute = nameof(HasBenchmark))]
    private async Task ExportReportAsync()
    {
        if (LastBenchmark is not { Ranked.Count: > 0 } report) return;
        var dlg = new SaveFileDialog
        {
            Title = "Exporter le benchmark DNS",
            FileName = $"aurum-benchmark-dns-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            Filter = "Texte (*.txt)|*.txt|Tous les fichiers (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        var text = DnsBenchmarkTextReport.Render(report, DateTime.UtcNow);
        try
        {
            await File.WriteAllTextAsync(dlg.FileName, text);
            Status = "Benchmark DNS exporté.";
        }
        catch (IOException ex) { Status = $"Export impossible : {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { Status = $"Export impossible : {ex.Message}"; }
    }
}
