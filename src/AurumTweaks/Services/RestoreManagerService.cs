using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AurumTweaks.Converters;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// Parses a WMI/CIM datetime string (the format <c>Get-ComputerRestorePoint</c> hands back for
/// <c>CreationTime</c>, e.g. <c>20260613143000.000000-000</c> = yyyyMMddHHmmss.ffffff±UUU where ±UUU is the
/// minutes offset from UTC). Pure so the date handling is unit-tested without spawning PowerShell. <b>Honesty
/// rule:</b> any malformation returns <c>false</c> so the caller shows « — », never a fabricated date — a wrong
/// timestamp on a restore point is worse than none.
/// </summary>
public static class WmiDateParser
{
    public static bool TryParse(string? wmi, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(wmi)) return false;
        var s = wmi.Trim();
        // Need the full yyyyMMddHHmmss.ffffff±UUU (25 chars). Restore-point stamps always carry the offset;
        // anything shorter or with a non-numeric offset (e.g. WMI's "***" unknown) is treated as unreadable.
        if (s.Length < 25) return false;

        if (!int.TryParse(s.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)) return false;
        if (!int.TryParse(s.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var month)) return false;
        if (!int.TryParse(s.AsSpan(6, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var day)) return false;
        if (!int.TryParse(s.AsSpan(8, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hour)) return false;
        if (!int.TryParse(s.AsSpan(10, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var minute)) return false;
        if (!int.TryParse(s.AsSpan(12, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var second)) return false;

        var sign = s[21];
        if (sign != '+' && sign != '-') return false;
        if (!int.TryParse(s.AsSpan(22, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var offsetMin)) return false;

        try
        {
            var offset = TimeSpan.FromMinutes(sign == '-' ? -offsetMin : offsetMin);
            result = new DateTimeOffset(year, month, day, hour, minute, second, offset);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            // An out-of-range component (month 13, day 32, offset > 14 h) means the string is not a real stamp.
            return false;
        }
    }
}

/// <summary>Maps the numeric <c>RestorePointType</c> Windows records to a stable FR label. Unknown codes get a
/// neutral « Point de restauration » rather than a guessed cause — the label never claims more than Windows said.</summary>
public static class RestorePointTypeInfo
{
    public static string Label(int type) => type switch
    {
        0 => "Installation d'application",
        1 => "Désinstallation d'application",
        10 => "Installation de pilote",
        12 => "Modification du système",
        13 => "Opération annulée",
        _ => "Point de restauration"
    };
}

/// <summary>One restore point exactly as Windows reports it. <see cref="CreatedAt"/> is null when the WMI stamp
/// could not be parsed, so <see cref="WhenDisplay"/> shows « — » instead of a fabricated date.</summary>
public sealed record RestorePoint(int SequenceNumber, string Description, int TypeRaw, DateTimeOffset? CreatedAt)
{
    public bool HasDate => CreatedAt.HasValue;
    public string TypeLabel => RestorePointTypeInfo.Label(TypeRaw);
    public string DescriptionDisplay => string.IsNullOrWhiteSpace(Description) ? "(sans description)" : Description.Trim();

    // Formats the wall-clock instant the stamp records (its own offset), so the display is deterministic and
    // independent of the machine's current time zone — and reuses the recorded time, never a converted guess.
    public string WhenDisplay => CreatedAt is { } d ? d.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) : "—";

    // Coarse "time since" via the shared pure RelativeTime; empty when undated so the row hides the age.
    public string AgeDisplay => CreatedAt is { } d ? RelativeTime.Since(d.UtcDateTime, DateTime.UtcNow) : string.Empty;

    public string SummaryDisplay => $"#{SequenceNumber} · {TypeLabel}";
}

/// <summary>
/// Parses the <c>ConvertTo-Csv</c> output of <c>Get-ComputerRestorePoint | Select SequenceNumber,Description,
/// RestorePointType,CreationTime</c>. Header-keyed (not positional) so a column reorder can't shift data — the same
/// approach as the drive / scheduled-task parsers, reusing the shared RFC-4180 <see cref="CsvRow"/> splitter. A row
/// without a real integer sequence number is dropped (never invented); points come back newest-first.
/// </summary>
public static class RestorePointCsvParser
{
    public static IReadOnlyList<RestorePoint> Parse(string? csv)
    {
        var points = new List<RestorePoint>();
        if (string.IsNullOrEmpty(csv)) return points;

        Dictionary<string, int>? columns = null;
        foreach (var rawLine in csv.Split('\n'))
        {
            var line = rawLine.Trim();                        // also strips the trailing '\r' from CRLF output
            if (line.Length == 0 || line[0] == '#') continue; // blank / a stray #TYPE header

            var fields = CsvRow.Split(line);

            if (columns is null)
            {
                // The first real line is the header; map column name → index so reads are by name.
                columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < fields.Count; i++) columns[fields[i].Trim()] = i;
                continue;
            }

            if (!TryField(fields, columns, "SequenceNumber", out var seqText) ||
                !int.TryParse(seqText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq))
                continue; // no real sequence number → not a point we can trust

            TryField(fields, columns, "Description", out var description);
            var typeRaw = TryField(fields, columns, "RestorePointType", out var typeText)
                          && int.TryParse(typeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t)
                ? t : -1; // unknown type → neutral label, never a guessed cause

            DateTimeOffset? created = TryField(fields, columns, "CreationTime", out var when)
                                      && WmiDateParser.TryParse(when, out var dt)
                ? dt : null;

            points.Add(new RestorePoint(seq, description ?? string.Empty, typeRaw, created));
        }

        // Sequence numbers increase monotonically, so descending = newest first.
        return points.OrderByDescending(p => p.SequenceNumber).ToList();
    }

    private static bool TryField(IReadOnlyList<string> fields, IReadOnlyDictionary<string, int> columns, string name, out string value)
    {
        if (columns.TryGetValue(name, out var idx) && idx < fields.Count)
        {
            value = fields[idx];
            return true;
        }
        value = string.Empty;
        return false;
    }
}

/// <summary>
/// The on-system state of <c>SystemRestorePointCreationFrequency</c> (HKLM SystemRestore). Absent or 1440 means
/// Windows' default one-point-per-24h throttle; 0 lifts it so every request creates a point. The gates mean no
/// button ever re-writes the held value, and an absent key honestly reads as « limite par défaut » (never invented).
/// </summary>
public sealed record RestoreFrequencyState(string? LiveValue, bool IsPresent)
{
    public bool IsUnthrottled => IsPresent && RegistryValue.Matches(LiveValue, "0", RegistryValueType.DWord);
    public bool IsDefault => !IsPresent || RegistryValue.Matches(LiveValue, "1440", RegistryValueType.DWord);
    public bool IsCustom => IsPresent && !IsUnthrottled && !IsDefault;

    public bool CanUnthrottle => !IsUnthrottled;
    public bool CanRestoreThrottle => IsPresent && !IsDefault; // a present non-default (incl. 0) can be reset to default

    public string StateDisplay =>
        IsUnthrottled ? "Sans limite · chaque demande crée un point"
        : IsDefault ? "Limite Windows par défaut · un point / 24 h"
        : $"Personnalisé · {LiveValue} min";
}

/// <summary>
/// The measured result of a checkpoint request. <see cref="Created"/> is true only when the point count actually
/// rose (before/after, like the memory-flush outcome) — so a Checkpoint-Computer call that Windows silently skipped
/// under the 24h throttle reports <see cref="Throttled"/>, never a fake « créé ». <see cref="Unmeasured"/> covers the
/// honest "ran but we couldn't list the points to verify" case.
/// </summary>
public sealed record CheckpointOutcome(bool Invoked, bool Measured, int BeforeCount, int AfterCount)
{
    public static CheckpointOutcome Failed { get; } = new(false, false, 0, 0);

    public bool Created => Invoked && Measured && AfterCount > BeforeCount;
    public bool Throttled => Invoked && Measured && AfterCount <= BeforeCount;
    public bool Unmeasured => Invoked && !Measured;

    public string Headline =>
        !Invoked ? "Échec : Windows a refusé la création du point de restauration."
        : !Measured ? "Demande envoyée, mais le résultat n'a pas pu être vérifié."
        : Created ? "Point de restauration créé."
        : "Windows n'a pas créé de nouveau point (un point existe déjà dans les dernières 24 h). Lève la limite ci-dessous pour forcer.";
}

/// <summary>The whole page's state: the live point list, the throttle setting, and whether the read succeeded.
/// <see cref="QueryOk"/> false (couldn't read) is kept distinct from a real empty list (« aucun point »).</summary>
public sealed record RestoreOverview(bool QueryOk, IReadOnlyList<RestorePoint> Points, RestoreFrequencyState Frequency)
{
    public int Count => Points.Count;
    public bool HasPoints => Points.Count > 0;
    public RestorePoint? Latest => Points.Count > 0 ? Points[0] : null; // newest-first

    public string Headline =>
        !QueryOk ? "Lecture impossible (protection système désactivée ou service indisponible)."
        : Count == 0 ? "Aucun point de restauration. Crées-en un avant toute modification importante."
        : $"{Count} point(s) · le plus récent : {Latest!.WhenDisplay}.";

    public static RestoreOverview Failed(RestoreFrequencyState frequency)
        => new(false, Array.Empty<RestorePoint>(), frequency);
}

/// <summary>
/// User-facing front-end for Windows System Restore — the safety-net surface that pairs with the app's existing
/// "create a restore point before applying tweaks" promise. It REUSES the low-level <see cref="IRestorePointService"/>
/// (genuine Checkpoint-Computer creation) and <see cref="IRegistryService"/> (the throttle lever), and lists points
/// via <see cref="ProcessRunner"/> — mirroring how the Services/Process pages are front-ends over lower services.
/// <b>The actual restore is never faked here:</b> it is handed off to Windows' own <c>rstrui.exe</c> wizard by the VM.
/// </summary>
public sealed class RestoreManagerService : IRestoreManagerService
{
    private const string SrKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    private const string FreqValue = "SystemRestorePointCreationFrequency";

    // Select exactly the four columns the parser keys on; ConvertTo-Csv keeps CreationTime as the raw WMI string.
    private const string ListCommand =
        "-NoProfile -NonInteractive -Command \"Get-ComputerRestorePoint | " +
        "Select-Object SequenceNumber,Description,RestorePointType,CreationTime | ConvertTo-Csv -NoTypeInformation\"";

    private readonly IRestorePointService _restore;
    private readonly IRegistryService _registry;

    public RestoreManagerService(IRestorePointService restore, IRegistryService registry)
    {
        _restore = restore;
        _registry = registry;
    }

    public Task<RestoreOverview> GetOverviewAsync() => Task.Run(() =>
    {
        var (ok, points) = ReadPoints();
        var present = _registry.TryReadValue("HKLM", SrKey, FreqValue, out var freq);
        return new RestoreOverview(ok, points, new RestoreFrequencyState(present ? freq : null, present));
    });

    public async Task<CheckpointOutcome> CreateCheckpointAsync(string description)
    {
        var desc = string.IsNullOrWhiteSpace(description) ? "Point Aurum" : description.Trim();
        var before = await Task.Run(ReadPoints);
        var ok = await _restore.CreateAsync(desc);
        if (!ok) return CheckpointOutcome.Failed;
        var after = await Task.Run(ReadPoints);
        // Only trust the before/after delta when both reads succeeded; otherwise report the honest "unmeasured" case.
        var measured = before.ok && after.ok;
        return new CheckpointOutcome(true, measured, before.points.Count, after.points.Count);
    }

    public Task<bool> SetUnthrottledAsync(bool unthrottle) => Task.Run(() =>
        unthrottle
            ? _registry.WriteValue("HKLM", SrKey, FreqValue, "0", RegistryValueType.DWord)
            : _registry.DeleteValue("HKLM", SrKey, FreqValue)); // remove → absent = Windows default throttle (true inverse)

    // Reuse the low-level enable (elevated Enable-ComputerRestore on the system drive). We discard its optimistic bool —
    // « it ran » is not « it took » — because the VM re-reads the overview afterwards and QueryOk is the honest proof.
    public Task EnableProtectionAsync() => _restore.EnableSystemRestoreIfDisabledAsync();

    private (bool ok, IReadOnlyList<RestorePoint> points) ReadPoints()
    {
        var (exit, stdout) = ProcessRunner.Capture("powershell.exe", ListCommand, 30_000);
        if (exit != 0) return (false, Array.Empty<RestorePoint>());
        return (true, RestorePointCsvParser.Parse(stdout));
    }
}
