using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace AurumTweaks.Services;

/// <summary>The kind of storage medium, as Windows classifies it. Drives whether the page talks about « usure SSD »
/// or « défragmentation HDD », and is reported, never guessed.</summary>
public enum DriveMedia { Unknown, Hdd, Ssd, Scm }

/// <summary>
/// Maps a drive's medium token to <see cref="DriveMedia"/> and a stable French label — pure and tested. It accepts
/// <b>both</b> the numeric <c>MSFT_PhysicalDisk.MediaType</c> code (3/4/5) and the friendly enum name PowerShell may
/// serialise instead (« HDD »/« SSD »/« SCM »): the wire form differs across Windows / Storage-module versions, so we
/// map both rather than bet on one and silently mislabel a drive.
/// </summary>
public static class DriveMediaCatalog
{
    public static DriveMedia FromToken(string? token)
    {
        var t = token?.Trim();
        if (string.IsNullOrEmpty(t)) return DriveMedia.Unknown;
        if (t.Equals("HDD", StringComparison.OrdinalIgnoreCase) || t == "3") return DriveMedia.Hdd;
        if (t.Equals("SSD", StringComparison.OrdinalIgnoreCase) || t == "4") return DriveMedia.Ssd;
        if (t.Equals("SCM", StringComparison.OrdinalIgnoreCase) || t == "5") return DriveMedia.Scm;
        return DriveMedia.Unknown;
    }

    public static string Label(DriveMedia media) => media switch
    {
        DriveMedia.Hdd => "Disque dur (HDD)",
        DriveMedia.Ssd => "SSD",
        DriveMedia.Scm => "Mémoire persistante (SCM)",
        _ => "Type inconnu"
    };
}

/// <summary>The drive's health as Windows itself reports it — the authoritative verdict the page leads with.</summary>
public enum DriveHealth { Unknown, Healthy, Warning, Unhealthy }

/// <summary>
/// Maps the <c>MSFT_PhysicalDisk.HealthStatus</c> token to <see cref="DriveHealth"/> and a French label — pure and
/// tested. Like <see cref="DriveMediaCatalog"/> it accepts both the numeric code (0/1/2) and the friendly name
/// (« Healthy »/« Warning »/« Unhealthy »), so the parse is immune to which form the running Windows serialises.
/// </summary>
public static class DriveHealthCatalog
{
    public static DriveHealth FromToken(string? token)
    {
        var t = token?.Trim();
        if (string.IsNullOrEmpty(t)) return DriveHealth.Unknown;
        if (t.Equals("Healthy", StringComparison.OrdinalIgnoreCase) || t == "0") return DriveHealth.Healthy;
        if (t.Equals("Warning", StringComparison.OrdinalIgnoreCase) || t == "1") return DriveHealth.Warning;
        if (t.Equals("Unhealthy", StringComparison.OrdinalIgnoreCase) || t == "2") return DriveHealth.Unhealthy;
        return DriveHealth.Unknown;
    }

    public static string Label(DriveHealth health) => health switch
    {
        DriveHealth.Healthy => "Sain",
        DriveHealth.Warning => "Avertissement",
        DriveHealth.Unhealthy => "Défaillant",
        _ => "Inconnu"
    };
}

/// <summary>Display helpers for the bus a drive sits on — pure. We only need it to name the connection and to flag a
/// USB-bridged drive, whose SMART/reliability data often can't pass through the bridge (so empty fields there mean
/// « unknown », not « healthy »). Accepts the numeric <c>BusType</c> code or the friendly name.</summary>
public static class DriveBus
{
    public static string Describe(string? token)
    {
        var t = token?.Trim();
        if (string.IsNullOrEmpty(t)) return "Bus inconnu";
        return t switch
        {
            "7" => "USB",
            "8" => "RAID",
            "9" => "iSCSI",
            "10" => "SAS",
            "11" => "SATA",
            "17" => "NVMe",
            _ => t   // already a friendly name ("SATA"/"NVMe"/"USB"/…) or an uncommon code we pass through verbatim
        };
    }

    public static bool IsUsb(string? token)
    {
        var t = token?.Trim();
        return t == "7" || (t is not null && t.IndexOf("USB", StringComparison.OrdinalIgnoreCase) >= 0);
    }
}

/// <summary>
/// Pure formatters for the reliability metrics — fixed to fr-FR for the decimal comma and deterministic tests, and
/// honest about absence: a metric Windows didn't report comes through as « — », never a fabricated zero.
/// </summary>
public static class DriveHealthFormat
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private const double HoursPerYear = 8766.0; // 365.25 × 24 — keeps the « ≈ N ans » approximation honest

    public static string Temperature(int? celsius) => celsius is { } c ? $"{c} °C" : "—";

    public static string Wear(int? percent) => percent is { } p ? $"{p} %" : "—";

    public static string PowerOnHours(long? hours)
    {
        if (hours is not { } h || h < 0) return "—";
        if (h < HoursPerYear) return $"{h} h";
        double years = h / HoursPerYear;
        string unit = years < 2 ? "an" : "ans";
        return $"{h} h (≈ {years.ToString("0.#", Fr)} {unit})";
    }
}

/// <summary>The page's overall read on a drive — derived from Windows' health plus the measured reliability signals.</summary>
public enum DriveVerdict { Unknown, Healthy, Watch, Critical }

/// <summary>
/// The load-bearing honesty core: turns Windows' health plus the measured wear / uncorrected-error counters into a
/// verdict — pure, errors-first, and pinned by tests. Windows' own <see cref="DriveHealth"/> outranks everything (a
/// drive Windows calls « Unhealthy » is <see cref="DriveVerdict.Critical"/>); our derived signals can only escalate a
/// healthy/unknown drive as far as <see cref="DriveVerdict.Watch"/> — a high wear count or a past error is a reason to
/// keep an eye on the drive and back up, <b>not</b> proof it's dying, so we never fabricate a « failing » verdict from
/// them alone.
/// </summary>
public static class DriveHealthVerdict
{
    /// <summary>Wear is reported as a percentage consumed; past this we suggest watching the drive.</summary>
    public const int WearWatchThreshold = 80;

    public static (DriveVerdict Verdict, string Message) Evaluate(
        DriveHealth health, int? wearPercent, long? uncorrectedErrors)
    {
        // Windows' authoritative verdict first — it outranks any signal we derive ourselves.
        if (health == DriveHealth.Unhealthy)
            return (DriveVerdict.Critical,
                "Défaillance signalée par Windows — sauvegarde tes données sans attendre et envisage un remplacement.");

        if (health == DriveHealth.Warning)
            return (DriveVerdict.Watch,
                "Windows signale un avertissement sur ce disque — sauvegarde tes données importantes.");

        // Health is Healthy or Unknown: let the measured signals escalate, but never past « à surveiller ».
        if (wearPercent is { } w && w >= WearWatchThreshold)
            return (DriveVerdict.Watch,
                $"Usure élevée (~{w} % d'usure rapportée) — la fin de vie du disque approche, surveille-le.");

        if (uncorrectedErrors is { } e && e > 0)
            return (DriveVerdict.Watch,
                "Des erreurs non corrigées ont été enregistrées — surveille ce disque et sauvegarde tes données.");

        if (health == DriveHealth.Healthy)
            return (DriveVerdict.Healthy, "Sain — aucun problème signalé par Windows.");

        return (DriveVerdict.Unknown, "État inconnu — Windows n'expose pas l'état de santé de ce disque.");
    }
}

/// <summary>
/// One physical drive joined with its measured reliability counters. Every figure is what Windows reported for the
/// drive (capacity, health, temperature, wear, power-on hours, uncorrected errors); a counter Windows didn't expose is
/// a <c>null</c> here and renders as « — » downstream, so an absent metric never masquerades as a zero.
/// </summary>
public sealed record DriveHealthInfo(
    string FriendlyName,
    DriveMedia Media,
    DriveHealth Health,
    string BusToken,
    long SizeBytes,
    int? Temperature,
    int? WearPercent,
    long? PowerOnHours,
    long? UncorrectedErrors)
{
    public string Name => string.IsNullOrWhiteSpace(FriendlyName) ? "Disque" : FriendlyName.Trim();
    public string MediaDisplay => DriveMediaCatalog.Label(Media);
    public string HealthDisplay => DriveHealthCatalog.Label(Health);
    public string BusDisplay => DriveBus.Describe(BusToken);
    public bool IsUsb => DriveBus.IsUsb(BusToken);
    public string SizeDisplay => ByteSize.Format(SizeBytes);

    public string TemperatureDisplay => DriveHealthFormat.Temperature(Temperature);
    public string WearDisplay => DriveHealthFormat.Wear(WearPercent);
    public string PowerOnHoursDisplay => DriveHealthFormat.PowerOnHours(PowerOnHours);
    public string UncorrectedErrorsDisplay => UncorrectedErrors is { } e ? e.ToString(CultureInfo.InvariantCulture) : "—";

    private (DriveVerdict Verdict, string Message) Eval =>
        DriveHealthVerdict.Evaluate(Health, WearPercent, UncorrectedErrors);
    public DriveVerdict Verdict => Eval.Verdict;
    public string VerdictMessage => Eval.Message;

    /// <summary>Short French tag for the drive's verdict — the « Sain / À surveiller / Défaillant / Inconnu » badge.
    /// Lives on the model so the drive-health report and the system report share one wording instead of each re-deriving
    /// it.</summary>
    public string VerdictLabel => Verdict switch
    {
        DriveVerdict.Critical => "Défaillant",
        DriveVerdict.Watch => "À surveiller",
        DriveVerdict.Healthy => "Sain",
        _ => "Inconnu"
    };

    // Show a metric chip only when the number is real — keeps the absent-counter case honest in the UI.
    public bool HasTemperature => Temperature.HasValue;
    public bool HasWear => WearPercent.HasValue;
    public bool HasPowerOnHours => PowerOnHours is { } h && h >= 0;
    public bool HasErrors => UncorrectedErrors.HasValue;

    public bool ShowCritical => Verdict == DriveVerdict.Critical;
    public bool ShowWatch => Verdict == DriveVerdict.Watch;
    public bool ShowHealthy => Verdict == DriveVerdict.Healthy;
    public bool ShowUnknown => Verdict == DriveVerdict.Unknown;
}

/// <summary>The live drive-health picture: every physical disk with its verdict, plus whether the query itself
/// succeeded (so the page tells « no problems » apart from « couldn't read the system »).</summary>
public sealed record DriveHealthReport(IReadOnlyList<DriveHealthInfo> Drives, bool QueryOk)
{
    public int Count => Drives.Count;
    public int CriticalCount => Drives.Count(d => d.Verdict == DriveVerdict.Critical);
    public int WatchCount => Drives.Count(d => d.Verdict == DriveVerdict.Watch);
    public int HealthyCount => Drives.Count(d => d.Verdict == DriveVerdict.Healthy);

    /// <summary>The most severe verdict across all drives (Critical &gt; Watch &gt; Unknown &gt; Healthy) — drives the
    /// page's one-line headline. Explicit precedence rather than enum order so « Unknown » never reads as « better »
    /// than the « Healthy » it would outvote.</summary>
    public DriveVerdict Worst
    {
        get
        {
            if (CriticalCount > 0) return DriveVerdict.Critical;
            if (WatchCount > 0) return DriveVerdict.Watch;
            if (Drives.Any(d => d.Verdict == DriveVerdict.Unknown)) return DriveVerdict.Unknown;
            return Drives.Count > 0 ? DriveVerdict.Healthy : DriveVerdict.Unknown;
        }
    }

    /// <summary>One honest headline for the whole system — never cheerier than the worst drive, and keeping a failed
    /// query (« indisponible ») distinct from a genuine all-clear. Shared by the page and the shareable report so the
    /// paste says exactly what the screen shows, rather than re-deriving the wording in two places.</summary>
    public string Headline
    {
        get
        {
            if (!QueryOk) return "État des disques indisponible";
            return Worst switch
            {
                DriveVerdict.Critical => "Alerte : une défaillance de disque est signalée — sauvegarde tes données.",
                DriveVerdict.Watch    => "À surveiller : un disque mérite ton attention.",
                DriveVerdict.Healthy  => "Tous les disques sont sains.",
                _                     => "État de santé partiellement disponible."
            };
        }
    }
}

/// <summary>
/// Parses the flat CSV emitted by <c>Get-PhysicalDisk | Get-StorageReliabilityCounter</c> (projected into one row per
/// disk) into <see cref="DriveHealthInfo"/> records — pure, so it's pinned by tests. It keys on the <b>header names</b>
/// (not fixed positions) so a column re-order can't silently shift data, reuses the shared <see cref="CsvRow"/> splitter
/// for ConvertTo-Csv's quoting, and treats every empty/non-numeric reliability cell as <c>null</c> (« unknown ») rather
/// than a fabricated zero. Uncorrected errors are the sum of the read and write counters when either is present.
/// </summary>
public static class DrivePhysicalParser
{
    public static IReadOnlyList<DriveHealthInfo> Parse(string? csv)
    {
        var drives = new List<DriveHealthInfo>();
        if (string.IsNullOrWhiteSpace(csv)) return drives;

        Dictionary<string, int>? cols = null;
        foreach (var rawLine in csv.Split('\n'))
        {
            var line = rawLine.Trim();                        // also strips the trailing '\r' from CRLF output
            if (line.Length == 0 || line[0] == '#') continue; // blank / a stray #TYPE header

            var fields = CsvRow.Split(line);

            if (cols is null)
            {
                // The first non-blank line is ConvertTo-Csv's header row — build a name → column-index map from it.
                cols = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < fields.Count; i++) cols[fields[i].Trim()] = i;
                continue;
            }

            string Get(string name) =>
                cols.TryGetValue(name, out var idx) && idx < fields.Count ? fields[idx] : string.Empty;

            string friendly = Get("FriendlyName");
            // A row with no identity at all (name, media, and size all blank) isn't a real disk — skip it.
            if (string.IsNullOrWhiteSpace(friendly)
                && string.IsNullOrWhiteSpace(Get("MediaType"))
                && string.IsNullOrWhiteSpace(Get("Size"))) continue;

            long? read = ParseLong(Get("ReadErrorsUncorrected"));
            long? write = ParseLong(Get("WriteErrorsUncorrected"));
            long? uncorrected = read is null && write is null ? null : (read ?? 0) + (write ?? 0);

            drives.Add(new DriveHealthInfo(
                FriendlyName: friendly,
                Media: DriveMediaCatalog.FromToken(Get("MediaType")),
                Health: DriveHealthCatalog.FromToken(Get("HealthStatus")),
                BusToken: Get("BusType").Trim(),
                SizeBytes: ParseLong(Get("Size")) ?? 0,
                Temperature: ParseInt(Get("Temperature")),
                WearPercent: ParseInt(Get("Wear")),
                PowerOnHours: ParseLong(Get("PowerOnHours")),
                UncorrectedErrors: uncorrected));
        }
        return drives;
    }

    // Empty or non-numeric → null: an absent reliability counter is honestly « unknown », never 0.
    private static long? ParseLong(string? s) =>
        long.TryParse(s?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static int? ParseInt(string? s) =>
        int.TryParse(s?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
}

/// <summary>
/// The « Santé des disques » service — thin I/O glue over the pure cores above. Honest by construction: it reports the
/// real state Windows holds for each physical disk (<c>Get-PhysicalDisk</c>) joined to Windows' own interpretation of
/// the drive's SMART data (<c>Get-StorageReliabilityCounter</c> — temperature, wear, power-on hours, uncorrected
/// errors). It never decodes raw vendor SMART bytes itself, never invents a metric a drive doesn't expose, and offers
/// no « repair » action it couldn't actually perform — the page is a faithful read-only health view plus honest
/// hand-offs to Windows' own drive-optimisation and storage tools.
/// </summary>
public sealed class DriveHealthService : IDriveHealthService
{
    public Task<DriveHealthReport> GetReportAsync() => Task.Run(GetReport);

    private static DriveHealthReport GetReport()
    {
        // One PowerShell pass: project each physical disk joined to its reliability counter into a flat CSV. The
        // counter is Windows' OWN reading of the drive's SMART data — we surface that, not a home-grown decode. A
        // drive without a counter (USB bridge, some virtual disks) yields empty cells the parser keeps as « unknown ».
        var (_, stdout) = ProcessRunner.Capture("powershell.exe",
            "-NoProfile -NonInteractive -Command \"" +
            "Get-PhysicalDisk | ForEach-Object { " +
            "$r = $_ | Get-StorageReliabilityCounter; " +
            "[PSCustomObject]@{ " +
            "FriendlyName=$_.FriendlyName; MediaType=$_.MediaType; HealthStatus=$_.HealthStatus; " +
            "BusType=$_.BusType; Size=$_.Size; Temperature=$r.Temperature; Wear=$r.Wear; " +
            "PowerOnHours=$r.PowerOnHours; ReadErrorsUncorrected=$r.ReadErrorsUncorrected; " +
            "WriteErrorsUncorrected=$r.WriteErrorsUncorrected } } | ConvertTo-Csv -NoTypeInformation\"",
            timeoutMs: 30_000);

        var drives = DrivePhysicalParser.Parse(stdout);
        // Every PC has at least one physical disk; an empty list means the query failed (no Storage module / denied),
        // surfaced honestly as QueryOk=false rather than « 0 disque ».
        return new DriveHealthReport(drives, QueryOk: drives.Count > 0);
    }
}
