using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace AurumTweaks.ViewModels;

/// <summary>
/// Gaming page. Two things here are genuinely wired and honest: a launcher scan (Steam/Epic/… detection) and
/// a real network route measurement (ping / jitter / loss). The "Game Mode" heavy-lifting — power plan,
/// input latency, GPU scheduling, idle services — is deliberately NOT a fake toggle here: those land as
/// reversible tweaks (with a restore point) via <c>TweakService</c>, so this page routes the user to the
/// Tweaks tab and to Windows' own Game Mode rather than pretending to flip a switch that does nothing.
/// </summary>
public partial class GamingViewModel : ObservableObject
{
    private readonly IGameDetectionService _games;
    private readonly INetworkOptiService _network;

    public ObservableCollection<DetectedGame> Games { get; } = new();

    /// <summary>The traced route to <see cref="NetworkTarget"/> — populated by "Tracer la route".</summary>
    public ObservableCollection<TracerouteHop> RouteHops { get; } = new();

    /// <summary>Public DNS resolvers ranked fastest-first — populated by "Lancer le benchmark".</summary>
    public ObservableCollection<DnsProbeResult> DnsResults { get; } = new();

    [ObservableProperty] private string _networkTarget = "1.1.1.1";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RouteQuality))]
    [NotifyCanExecuteChangedFor(nameof(CopyNetworkDiagnosticCommand))]
    private NetworkRouteSnapshot? _lastRoute;

    [ObservableProperty] private string? _routeStatus;
    [ObservableProperty] private string? _dnsStatus;
    [ObservableProperty] private string? _dnsComparison;
    [ObservableProperty] private string? _diagnosticStatus;
    [ObservableProperty] private bool _isScanning = true;

    public GamingViewModel(IGameDetectionService games, INetworkOptiService network)
    {
        _games = games;
        _network = network;
        _ = ScanAsync();
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        IsScanning = true;
        Games.Clear();
        var list = await _games.ScanAsync();
        foreach (var g in list) Games.Add(g);
        IsScanning = false;
    }

    [RelayCommand]
    private async Task MeasureNetworkAsync()
    {
        LastRoute = await _network.MeasureAsync(NetworkTarget);
    }

    /// <summary>
    /// Honest connection-stability verdict for the last measurement — worst of loss/jitter/latency, clearly labelled
    /// indicative (it rates the path to the chosen target, never the game server). Null until a route is measured.
    /// </summary>
    public string? RouteQuality =>
        LastRoute is { } r ? Describe(NetworkQualityGrade.Assess(r)) : null;

    private static string Describe(NetworkQualityGrade g) => $"Stabilité : {g.Label} — {g.Detail}";

    /// <summary>
    /// Trace the real route to the target host (increasing-TTL ICMP). The command auto-disables while it runs;
    /// the worst-latency-jump hint is labelled "indicatif" because per-hop ICMP RTTs aren't monotonic.
    /// </summary>
    [RelayCommand]
    private async Task TraceRouteAsync()
    {
        RouteHops.Clear();
        RouteStatus = "Traçage de la route en cours…";
        var report = await _network.TraceRouteAsync(NetworkTarget);
        foreach (var h in report.Hops) RouteHops.Add(h);
        CopyNetworkDiagnosticCommand.NotifyCanExecuteChanged();

        var jump = TracerouteMath.BiggestLatencyJump(report.Hops);
        RouteStatus = jump is null
            ? report.Summary
            : $"{report.Summary} Plus forte hausse de latence au saut {jump.Ttl} (+{jump.DeltaMs} ms, indicatif).";
    }

    /// <summary>
    /// Benchmark the public DNS resolvers (real timed A-record queries) and list them fastest-first. The command
    /// auto-disables while running; a resolver that doesn't answer is shown honestly as "—", never a fake time.
    /// </summary>
    [RelayCommand]
    private async Task BenchmarkDnsAsync()
    {
        DnsResults.Clear();
        DnsComparison = null;
        DnsStatus = "Benchmark DNS en cours…";
        var report = await _network.BenchmarkDnsAsync();
        foreach (var r in report.Ranked) DnsResults.Add(r);
        DnsStatus = report.Summary;
        DnsComparison = DnsBenchmarkMath.CompareToCurrent(report);
        CopyNetworkDiagnosticCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Copy the real network measurements (latency + stability grade, traced route, DNS ranking) as a shareable text
    /// block — the way a connection problem is actually triaged on Discord is a paste. The render is the tested pure
    /// <see cref="NetworkDiagnosticReport"/>; this is thin clipboard glue. The command self-disables until at least one
    /// step has been measured, so it never copies an empty « tout non mesuré » sheet.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCopyNetworkDiagnostic))]
    private void CopyNetworkDiagnostic()
    {
        var text = NetworkDiagnosticReport.Render(NetworkTarget, LastRoute, RouteHops, DnsResults, DateTime.UtcNow);
        try
        {
            System.Windows.Clipboard.SetText(text);
            DiagnosticStatus = "Diagnostic réseau copié — colle-le où tu veux (Discord, forum…).";
        }
        catch
        {
            // The clipboard can be momentarily locked by another process — non-fatal, just say so.
            DiagnosticStatus = "Copie impossible (presse-papiers occupé). Réessaie dans un instant.";
        }
    }

    private bool CanCopyNetworkDiagnostic => LastRoute is not null || RouteHops.Count > 0 || DnsResults.Count > 0;

    /// <summary>Open the classic network-adapters panel where DNS is edited — the apply step we keep manual.</summary>
    [RelayCommand]
    private void OpenNetworkAdapters() => ShellLauncher.OpenLocal("ncpa.cpl");

    /// <summary>Open Windows' own Game Mode settings — the real toggle lives there; we don't fake our own.</summary>
    [RelayCommand]
    private void OpenWindowsGameMode() => ShellLauncher.OpenLink("ms-settings:gaming-gamemode");

    /// <summary>The heavy gaming optimizations ship as reversible tweaks (with a restore point) — go apply them.</summary>
    [RelayCommand]
    private void GoToTweaks() => App.Services.GetRequiredService<MainViewModel>().Navigate("Tweaks");
}
