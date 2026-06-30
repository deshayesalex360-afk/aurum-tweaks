using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// Persists and replays whole-catalogue state snapshots. The honesty-bearing parts are pure and tested elsewhere
/// (<see cref="SnapshotDiff.Compare"/> for the classification, <see cref="SnapshotPath.For"/> for path containment);
/// this is the thin I/O glue: probe the catalogue via <see cref="ITweakService.DetectStatesAsync"/>, attach each
/// tweak's localized name, and round-trip JSON under <c>%LOCALAPPDATA%\AurumTweaks\Snapshots</c>. Enums are written
/// as strings so a saved snapshot stays readable and survives a future reorder of <see cref="TweakAppliedState"/>.
/// </summary>
public sealed class SnapshotService : ISnapshotService
{
    private readonly ITweakRepository _repo;
    private readonly ITweakService _tweaks;
    private readonly ILocalizationService _localization;
    private readonly string _dir;

    public SnapshotService(ITweakRepository repo, ITweakService tweaks, ILocalizationService localization)
    {
        _repo = repo;
        _tweaks = tweaks;
        _localization = localization;
        _dir = Environment.ExpandEnvironmentVariables("%LOCALAPPDATA%\\AurumTweaks\\Snapshots");
        Directory.CreateDirectory(_dir);
    }

    public async Task<SystemSnapshot> CaptureAsync(string? label)
    {
        var snapshot = await BuildLiveAsync(label);
        await using var s = File.Create(SnapshotPath.For(_dir, snapshot.Id));
        await JsonSerializer.SerializeAsync(s, snapshot, SnapshotPortability.JsonOptions);
        return snapshot;
    }

    public Task<SystemSnapshot> CaptureLiveAsync(string? label) => BuildLiveAsync(label);

    // Probe every catalogue tweak in input order (DetectStatesAsync preserves it) and pair each state with the
    // tweak's id + localized display name. The name is captured NOW so the snapshot stays readable even if the
    // catalogue later renames or drops the tweak.
    private async Task<SystemSnapshot> BuildLiveAsync(string? label)
    {
        var catalog = await _repo.LoadAllAsync();
        var states = await _tweaks.DetectStatesAsync(catalog);
        var entries = new List<SnapshotEntry>(catalog.Count);
        for (var i = 0; i < catalog.Count; i++)
        {
            var name = _localization.GetLocalizedFrom(catalog[i].Name);
            if (string.IsNullOrWhiteSpace(name)) name = catalog[i].Id;
            entries.Add(new SnapshotEntry(catalog[i].Id, name, states[i]));
        }
        return new SystemSnapshot
        {
            Label = label?.Trim() ?? string.Empty,
            CapturedUtc = DateTime.UtcNow,
            // The build taking THIS capture, frozen into the record so a snapshot that's later exported, carried past a
            // reinstall, or shared still says which version produced it (BuildIdentity = the same source the Transparence
            // disclosure uses, so the two surfaces can't disagree on the running version).
            AppVersion = BuildIdentity.CurrentVersion,
            Entries = entries
        };
    }

    public async Task<IReadOnlyList<SystemSnapshot>> LoadAllAsync()
    {
        var list = new List<SystemSnapshot>();
        foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                await using var s = File.OpenRead(file);
                var snapshot = await JsonSerializer.DeserializeAsync<SystemSnapshot>(s, SnapshotPortability.JsonOptions);
                if (snapshot is not null) list.Add(snapshot);
            }
            catch (Exception ex)
            {
                // A single corrupt/locked file must never sink the whole list — skip it, keep the rest.
                Serilog.Log.Warning(ex, "Failed to load snapshot {File}", file);
            }
        }
        return list.OrderByDescending(s => s.CapturedUtc).ToList();
    }

    public Task DeleteAsync(string id)
    {
        var path = SnapshotPath.For(_dir, id);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    // Write one snapshot to a user-chosen file so a baseline can survive a reinstall or move between machines. The
    // SHAPE is the pure SnapshotPortability.Serialize; this is only the write glue.
    public Task ExportAsync(SystemSnapshot snapshot, string destinationPath)
        => File.WriteAllTextAsync(destinationPath, SnapshotPortability.Serialize(snapshot));

    // Read a portable file back into the store. Validation/normalization is the pure SnapshotPortability.TryImport
    // (fresh id, entry-less/garbage refusal); a bad file or an unreadable path surfaces as a SnapshotImportException
    // carrying a French, user-ready message the VM shows verbatim. On success the import is persisted under its fresh
    // id so it joins the saved list like any capture and survives a restart.
    public async Task<SystemSnapshot> ImportAsync(string sourcePath)
    {
        string json;
        try
        {
            json = await File.ReadAllTextAsync(sourcePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SnapshotImportException($"Lecture impossible : {ex.Message}");
        }

        if (!SnapshotPortability.TryImport(json, DateTime.UtcNow, out var snapshot, out var error))
            throw new SnapshotImportException(error!);

        await using var s = File.Create(SnapshotPath.For(_dir, snapshot!.Id));
        await JsonSerializer.SerializeAsync(s, snapshot, SnapshotPortability.JsonOptions);
        return snapshot;
    }
}

/// <summary>
/// Thrown by <see cref="SnapshotService.ImportAsync"/> when a chosen file can't be read or doesn't hold a usable
/// snapshot. Carries a French, user-ready message (the VM shows it verbatim). A typed exception — not a bool return —
/// keeps the import's failure channel cleanly separate from a successful <see cref="SystemSnapshot"/> result.
/// </summary>
public sealed class SnapshotImportException : Exception
{
    public SnapshotImportException(string message) : base(message) { }
}
