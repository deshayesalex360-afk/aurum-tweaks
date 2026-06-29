using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AurumTweaks.Services;

/// <summary>
/// Human-readable size formatter — pure, so it's pinned by tests. Base-1024 with French unit names (o / Ko / Mo / Go /
/// To) so the figures line up with what Windows Explorer shows the user for the very same folders. The culture is
/// fixed to fr-FR (comma decimal) rather than the ambient one so the output is both correct for the French UI and
/// deterministic in tests regardless of the build agent's locale.
/// </summary>
public static class ByteSize
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly string[] Units = { "o", "Ko", "Mo", "Go", "To", "Po" };

    public static string Format(long bytes)
    {
        if (bytes <= 0) return "0 o";

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // Octets and kilo-octets read better as whole-ish numbers; from Mo up, one or two decimals carry real signal.
        string format = unit switch
        {
            0 => "0",        // octets — always integral
            1 => "0.#",      // Ko — at most one decimal
            _ => "0.##"      // Mo and beyond — up to two decimals
        };
        return value.ToString(format, Fr) + " " + Units[unit];
    }
}

/// <summary>A curated, known-safe disk location the app measures and can clear in place.</summary>
public enum CleanupCategory
{
    /// <summary>The current user's temporary folder (<c>%TEMP%</c>).</summary>
    UserTemp,

    /// <summary>The machine temporary folder (<c>C:\Windows\Temp</c>).</summary>
    WindowsTemp,

    /// <summary>Application crash dumps (<c>%LOCALAPPDATA%\CrashDumps</c>).</summary>
    CrashDumps,

    /// <summary>The Windows Update download cache (<c>%SystemRoot%\SoftwareDistribution\Download</c>).</summary>
    WindowsUpdateCache
}

/// <summary>One curated cleanup location: its category, French label, and honest advice. Pure metadata — no path
/// (paths are machine-specific and resolved by the service) and no behaviour.</summary>
public sealed record CleanupTarget(CleanupCategory Category, string Label, string Advice);

/// <summary>
/// The curated set of disk locations the app is willing to clear automatically — pure and tested. The bar for
/// inclusion is deliberately high: every entry is a folder Windows or applications <b>recreate on demand</b>, so
/// clearing it is safe even though file deletion is irreversible. Risky reclaimable space (WinSxS, Windows.old, the
/// Recycle Bin, restore points) is intentionally <b>absent</b> here — the page hands those to Windows' own cleanmgr
/// rather than automate a deletion that could remove a rollback or break the component store.
/// </summary>
public static class CleanupTargetCatalog
{
    public static IReadOnlyList<CleanupTarget> Targets { get; } = new[]
    {
        new CleanupTarget(CleanupCategory.UserTemp,
            "Fichiers temporaires (utilisateur)",
            "Le dossier %TEMP% de ta session. Les installateurs et applications y laissent des restes que Windows recrée au besoin."),
        new CleanupTarget(CleanupCategory.WindowsTemp,
            "Fichiers temporaires (Windows)",
            "Le dossier C:\\Windows\\Temp, partagé par le système. Sans danger à vider ; recréé automatiquement."),
        new CleanupTarget(CleanupCategory.CrashDumps,
            "Vidages sur incident (crash dumps)",
            "Les fichiers de débogage laissés par les applications qui ont planté. Inutiles une fois le problème passé."),
        new CleanupTarget(CleanupCategory.WindowsUpdateCache,
            "Cache de téléchargement Windows Update",
            "Les installeurs déjà téléchargés par Windows Update. Re-téléchargés au besoin. Une mise à jour en cours peut verrouiller certains fichiers — ils seront ignorés.")
    };

    public static string Label(CleanupCategory category) =>
        Targets.FirstOrDefault(t => t.Category == category)?.Label ?? category.ToString();
}

/// <summary>One curated location joined with its real measured size on this machine. <see cref="Bytes"/> is the sum of
/// actual file lengths read from disk — never an estimate — and <see cref="Exists"/> tells the page when a folder is
/// simply not present (so "absent" and "empty" stay distinct).</summary>
public sealed record CleanupItem(CleanupTarget Target, long Bytes, bool Exists)
{
    public CleanupCategory Category => Target.Category;
    public string Label => Target.Label;
    public string Advice => Target.Advice;
    public string SizeDisplay => ByteSize.Format(Bytes);

    /// <summary>True only when there's a real, non-zero amount to reclaim — drives whether a « Nettoyer » button shows
    /// (no dead button on an already-empty or absent folder).</summary>
    public bool HasReclaimable => Exists && Bytes > 0;
}

/// <summary>The scan picture: every curated location with its measured size. <see cref="TotalBytes"/> is the honest
/// sum the page headlines.</summary>
public sealed record CleanupReport(IReadOnlyList<CleanupItem> Items)
{
    public long TotalBytes => Items.Sum(i => i.Bytes);
    public string TotalDisplay => ByteSize.Format(TotalBytes);

    /// <summary>How many locations actually have something to reclaim — for the "N emplacement(s)…" status line.</summary>
    public int ReclaimableCount => Items.Count(i => i.HasReclaimable);
}

/// <summary>
/// The outcome of a real clean, expressed as the bytes present <b>before</b> and <b>after</b> deletion — pure, so the
/// load-bearing honesty rule is pinned by a test: the figure the UI shows as "libéré" is <c>Before − After</c> (the
/// space that genuinely disappeared), never the pre-scan estimate. Clamped at zero so a folder that grew during the
/// operation can't produce a negative or fabricated number, and <see cref="FullyCleared"/> is false whenever locked
/// files survived — the page stays honest about a partial clean instead of claiming a clean sweep.
/// </summary>
public sealed record CleanupOutcome(long BytesBefore, long BytesAfter)
{
    public long Freed => Math.Max(0, BytesBefore - BytesAfter);
    public string FreedDisplay => ByteSize.Format(Freed);

    /// <summary>True when nothing reclaimable remains — i.e. no locked/in-use files were left behind.</summary>
    public bool FullyCleared => BytesAfter <= 0;

    public static CleanupOutcome Empty { get; } = new(0, 0);

    /// <summary>Sum several per-folder outcomes into one (for a "tout nettoyer" pass).</summary>
    public static CleanupOutcome Sum(IEnumerable<CleanupOutcome> outcomes)
    {
        long before = 0, after = 0;
        foreach (var o in outcomes) { before += o.BytesBefore; after += o.BytesAfter; }
        return new CleanupOutcome(before, after);
    }
}

/// <summary>
/// The « Nettoyage disque » manager — thin I/O glue over the pure cores above. Honest by construction: it reports the
/// real measured size of a curated set of known-safe temp/cache folders, and a clean genuinely deletes their contents
/// best-effort (skipping anything locked) then <b>re-measures</b> so the "espace libéré" it shows is the space that
/// actually disappeared — never the optimistic pre-scan estimate. It deliberately touches only folders Windows
/// recreates on demand; riskier reclaimable space (WinSxS, Windows.old, the Recycle Bin, restore points) is left to
/// Windows' own cleanmgr, which the page links to instead of automating an irreversible deletion of rollback data.
/// </summary>
public sealed class DiskCleanupService : IDiskCleanupService
{
    public Task<CleanupReport> ScanAsync() => Task.Run(Scan);

    public Task<CleanupOutcome> CleanAsync(CleanupCategory category) => Task.Run(() => Clean(category));

    public Task<CleanupOutcome> CleanAllAsync() => Task.Run(CleanAll);

    private static CleanupReport Scan()
    {
        var items = new List<CleanupItem>(CleanupTargetCatalog.Targets.Count);
        foreach (var target in CleanupTargetCatalog.Targets)
        {
            string? path = ResolvePath(target.Category);
            bool exists = path != null && Directory.Exists(path);
            long bytes = exists ? MeasureDir(path!) : 0;
            items.Add(new CleanupItem(target, bytes, exists));
        }
        return new CleanupReport(items);
    }

    private static CleanupOutcome Clean(CleanupCategory category)
    {
        string? path = ResolvePath(category);
        if (path == null || !Directory.Exists(path)) return CleanupOutcome.Empty;

        long before = MeasureDir(path);
        ClearContents(path);
        long after = MeasureDir(path);
        return new CleanupOutcome(before, after);
    }

    private static CleanupOutcome CleanAll() =>
        CleanupOutcome.Sum(CleanupTargetCatalog.Targets.Select(t => Clean(t.Category)));

    /// <summary>Map a curated category to its absolute path on THIS machine. Kept in the service (not the pure catalog)
    /// because every one of these is environment-relative.</summary>
    private static string? ResolvePath(CleanupCategory category)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return category switch
        {
            CleanupCategory.UserTemp => Path.GetTempPath(),
            CleanupCategory.WindowsTemp => Path.Combine(windows, "Temp"),
            CleanupCategory.CrashDumps => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps"),
            CleanupCategory.WindowsUpdateCache => Path.Combine(windows, "SoftwareDistribution", "Download"),
            _ => null
        };
    }

    /// <summary>Sum the byte length of every file under <paramref name="root"/>, robustly. A manual stack walk
    /// (rather than <c>EnumerateFiles(AllDirectories)</c>, which aborts the whole scan on the first inaccessible
    /// subfolder) lets a single locked/denied directory be skipped without losing the rest, and reparse points are
    /// not followed so a junction can't loop us or double-count files that live elsewhere.</summary>
    private static long MeasureDir(string root)
    {
        long total = 0;
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            string dir = stack.Pop();

            try
            {
                foreach (string file in Directory.EnumerateFiles(dir))
                {
                    try { total += new FileInfo(file).Length; }
                    catch { /* vanished or denied between enumerate and stat — skip */ }
                }
            }
            catch { continue; }   // can't list this folder's files — skip it, keep walking the rest

            try
            {
                foreach (string sub in Directory.EnumerateDirectories(dir))
                {
                    try
                    {
                        if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch { continue; }
                    stack.Push(sub);
                }
            }
            catch { /* can't list subfolders — the files above are still counted */ }
        }
        return total;
    }

    /// <summary>Delete everything inside <paramref name="root"/> (but not the folder itself) best-effort: locked or
    /// in-use entries throw and are simply left behind, so this never aborts midway and the caller's re-measure
    /// captures exactly what survived.</summary>
    private static void ClearContents(string root)
    {
        try
        {
            foreach (string sub in Directory.EnumerateDirectories(root))
            {
                try { Directory.Delete(sub, recursive: true); } catch { /* locked/in-use — leave it */ }
            }
        }
        catch { /* couldn't enumerate subfolders — fall through to files */ }

        try
        {
            foreach (string file in Directory.EnumerateFiles(root))
            {
                try { File.Delete(file); } catch { /* locked/in-use — leave it */ }
            }
        }
        catch { /* couldn't enumerate files — best-effort done */ }
    }
}
