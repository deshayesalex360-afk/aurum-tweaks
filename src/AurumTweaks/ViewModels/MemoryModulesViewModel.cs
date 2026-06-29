using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace AurumTweaks.ViewModels;

public partial class MemoryModulesViewModel : ObservableObject
{
    private readonly IMemoryModulesService _service;
    private MemoryModulesReport? _lastReport;

    [ObservableProperty] private string? _status;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isBusy;

    [ObservableProperty] private bool _hasModules;

    // Gates « Copier le rapport »: enabled only once a real read has produced a report, so the button is never a no-op.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyReportCommand))]
    private bool _hasReport;

    [ObservableProperty] private string _moduleCountDisplay = "—";
    [ObservableProperty] private string _totalDisplay = "—";
    [ObservableProperty] private string _typeDisplay = "—";
    [ObservableProperty] private string _speedDisplay = "—";
    [ObservableProperty] private string _ratedDisplay = "—";
    [ObservableProperty] private string _slotsDisplay = "—";
    [ObservableProperty] private string _channelDisplay = "—";

    [ObservableProperty] private string _profileHeadline = "—";
    [ObservableProperty] private string _profileDetail = string.Empty;
    [ObservableProperty] private bool _profileWarn;
    [ObservableProperty] private bool _profileOk;
    [ObservableProperty] private bool _profileUnknown = true;

    public ObservableCollection<MemoryModuleRow> Modules { get; } = new();

    public MemoryModulesViewModel(IMemoryModulesService service)
    {
        _service = service;
        _ = RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        Status = "Lecture des modules mémoire…";
        try
        {
            var rep = await _service.GetReportAsync();
            _lastReport = rep;

            Modules.Clear();
            foreach (var row in rep.Modules) Modules.Add(row);

            HasModules = rep.HasModules;
            HasReport = true;
            ModuleCountDisplay = rep.HasModules ? rep.ModuleCount.ToString() : "—";
            TotalDisplay = rep.TotalDisplay;
            TypeDisplay = rep.TypeDisplay;
            SpeedDisplay = rep.SpeedDisplay;
            RatedDisplay = rep.RatedDisplay;
            SlotsDisplay = rep.SlotsDisplay;
            ChannelDisplay = rep.ChannelDisplay;

            ProfileHeadline = rep.ProfileHeadline;
            ProfileDetail = rep.ProfileDetail;
            ProfileWarn = rep.ProfileWarn;
            ProfileOk = rep.ProfileOk;
            ProfileUnknown = rep.ProfileUnknown;

            Status = rep.HasModules
                ? $"{rep.ModuleCount} module(s) détecté(s)."
                : "Aucun module détecté.";
        }
        catch (Exception ex)
        {
            Status = $"Échec de la lecture : {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRefresh() => !IsBusy;

    // Honest hand-offs: the actual XMP/EXPO toggle lives in the BIOS, and the timings calculator
    // is the right next step once the profile is active. Both are real in-app navigations.
    [RelayCommand]
    private void GoToBios() => App.Services.GetRequiredService<MainViewModel>().Navigate("Bios");

    [RelayCommand]
    private void GoToRamCalc() => App.Services.GetRequiredService<MainViewModel>().Navigate("RamCalc");

    /// <summary>Copy the shareable memory-layout paste — the real read-back state, never sent anywhere — to the clipboard.</summary>
    [RelayCommand(CanExecute = nameof(HasReport))]
    private void CopyReport()
    {
        if (_lastReport is null) return;
        var text = MemoryModulesTextReport.Render(_lastReport, DateTime.UtcNow);
        try
        {
            System.Windows.Clipboard.SetText(text);
            Status = "Rapport copié dans le presse-papiers.";
        }
        catch (Exception)
        {
            Status = "Impossible d'accéder au presse-papiers pour l'instant.";
        }
    }
}
