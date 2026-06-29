using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AurumTweaks.ViewModels;

public partial class MonitoringViewModel : ObservableObject
{
    private const int HistoryLength = 90;  // 90 samples = 90 seconds at 1Hz polling
    private const int MaxRecent = 120;      // bounded real-sample buffer feeding the shareable snapshot (~2 min)

    private readonly IMonitoringService _monitoring;

    // Only REAL polled snapshots land here (never the seed zeros the sparklines start with), so the shareable
    // « actuel · moyenne · pic » stats are honest. Written on the monitoring timer thread, read on the UI thread
    // when the user copies — hence the lock.
    private readonly object _bufferLock = new();
    private readonly List<MonitoringSnapshot> _recent = new();

    [ObservableProperty] private MonitoringSnapshot? _snapshot;
    [ObservableProperty] private string? _copyStatus;

    public ObservableCollection<double> CpuHistory { get; } = new();
    public ObservableCollection<double> GpuHistory { get; } = new();
    public ObservableCollection<double> CpuTempHistory { get; } = new();
    public ObservableCollection<double> GpuTempHistory { get; } = new();
    public ObservableCollection<double> RamHistory { get; } = new();

    public MonitoringViewModel(IMonitoringService monitoring)
    {
        _monitoring = monitoring;
        for (int i = 0; i < HistoryLength; i++)
        {
            CpuHistory.Add(0); GpuHistory.Add(0);
            CpuTempHistory.Add(0); GpuTempHistory.Add(0);
            RamHistory.Add(0);
        }
        _monitoring.SnapshotReady += (_, s) =>
        {
            Snapshot = s;
            Push(CpuHistory, s.CpuUsagePercent);
            Push(GpuHistory, s.GpuUsagePercent);
            Push(CpuTempHistory, s.CpuTempC);
            Push(GpuTempHistory, s.GpuTempC);
            Push(RamHistory, s.RamUsagePercent);
            lock (_bufferLock)
            {
                _recent.Add(s);
                if (_recent.Count > MaxRecent) _recent.RemoveAt(0);
            }
        };
        _monitoring.Start();
    }

    /// <summary>Copy a live CPU/GPU/RAM snapshot (actuel · moyenne · pic over the recent real samples) to the clipboard.</summary>
    [RelayCommand]
    private void CopySnapshot()
    {
        MonitoringSnapshot[] samples;
        lock (_bufferLock) samples = _recent.ToArray();

        if (samples.Length == 0)
        {
            CopyStatus = "Aucune mesure disponible pour l'instant — laisse le monitoring tourner une seconde.";
            return;
        }

        var text = MonitoringSnapshotTextReport.Render(samples, DateTime.UtcNow);
        try
        {
            System.Windows.Clipboard.SetText(text);
            CopyStatus = "Instantané copié dans le presse-papiers.";
        }
        catch (Exception)
        {
            CopyStatus = "Impossible d'accéder au presse-papiers pour l'instant.";
        }
    }

    private void Push(ObservableCollection<double> series, double value)
    {
        // Marshal to UI dispatcher
        var ui = System.Windows.Application.Current?.Dispatcher;
        if (ui is null || ui.CheckAccess())
        {
            DoPush(series, value);
        }
        else
        {
            ui.BeginInvoke(() => DoPush(series, value));
        }
    }

    private static void DoPush(ObservableCollection<double> series, double value)
    {
        if (series.Count >= HistoryLength) series.RemoveAt(0);
        series.Add(value);
    }
}
