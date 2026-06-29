using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AurumTweaks.Models;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the per-tweak technical disclosure (<see cref="TweakOperationSummary"/>) the Tweaks card shows under
/// "Détails techniques". The honesty stake: this text is derived from the same operation data the engine runs,
/// so it must render each operation class faithfully — the exact registry value, the service startup change,
/// the raw command — and it must DISCLOSE non-reversibility rather than paper over it. Every tier is nailed by
/// a value table here; a final guard walks the real shipped catalog so no operation ever renders a blank row.
/// </summary>
public class TweakOperationSummaryTests
{
    private static TweakOperation Reg(string hive, string key, string? name, string? apply, string? revert,
                                      RegistryValueType type = RegistryValueType.DWord)
        => new() { Type = OperationType.Registry, Hive = hive, Key = key, Name = name, Apply = apply, Revert = revert, ValueType = type };

    private static TweakOperation Svc(string name, string? apply, string? revert)
        => new() { Type = OperationType.Service, ServiceName = name, StartupApply = apply, StartupRevert = revert };

    private static TweakOperation Shell(OperationType type, string? script, string? revert)
        => new() { Type = type, Script = script, RevertScript = revert };

    // --- Registry: full path, value + type on apply, and delete-vs-write in both directions. ---

    [Fact]
    public void Registry_Set_RendersPathValueAndType()
    {
        var s = TweakOperationSummary.Describe(Reg("HKCU", "Software\\Microsoft\\GameBar", "AutoGameModeEnabled", "1", "0"));
        Assert.Equal("Registre", s.Kind);
        Assert.Equal("HKCU\\Software\\Microsoft\\GameBar\\AutoGameModeEnabled", s.Target);
        Assert.Equal("écrit 1 (DWORD)", s.Apply);
        Assert.Equal("écrit 0", s.Revert);
    }

    [Fact]
    public void Registry_ApplyNull_MeansDeleteOnApply()
    {
        var s = TweakOperationSummary.Describe(Reg("HKLM", "Key", "V", apply: null, revert: "1"));
        Assert.Equal("supprime la valeur", s.Apply);
        Assert.Equal("écrit 1", s.Revert);
    }

    [Fact]
    public void Registry_RevertNull_MeansDeleteOnRevert()
    {
        var s = TweakOperationSummary.Describe(Reg("HKLM", "Key", "V", apply: "1", revert: null));
        Assert.Equal("écrit 1 (DWORD)", s.Apply);
        Assert.Equal("supprime la valeur", s.Revert);
    }

    [Theory]
    [InlineData(RegistryValueType.DWord, "DWORD")]
    [InlineData(RegistryValueType.QWord, "QWORD")]
    [InlineData(RegistryValueType.String, "chaîne")]
    [InlineData(RegistryValueType.ExpandString, "chaîne extensible")]
    [InlineData(RegistryValueType.Binary, "binaire")]
    [InlineData(RegistryValueType.MultiString, "chaînes multiples")]
    public void Registry_ValueTypeLabel(RegistryValueType type, string label)
    {
        var s = TweakOperationSummary.Describe(Reg("HKLM", "K", "N", "5", "0", type));
        Assert.Equal($"écrit 5 ({label})", s.Apply);
    }

    [Fact]
    public void Registry_Target_JoinsOnlyPresentParts()
    {
        // A missing value name must not leave a stray trailing separator in the displayed path.
        var s = TweakOperationSummary.Describe(Reg("HKLM", "Some\\Key", name: null, apply: "1", revert: "0"));
        Assert.Equal("HKLM\\Some\\Key", s.Target);
    }

    // --- Service: name + startup type, both directions, French-labelled. ---

    [Fact]
    public void Service_RendersNameAndStartupBothDirections()
    {
        var s = TweakOperationSummary.Describe(Svc("WSearch", "Disabled", "DelayedAuto"));
        Assert.Equal("Service", s.Kind);
        Assert.Equal("WSearch", s.Target);
        Assert.Equal("démarrage → Désactivé", s.Apply);
        Assert.Equal("démarrage → Automatique (différé)", s.Revert);
    }

    [Theory]
    [InlineData("Disabled", "Désactivé")]
    [InlineData("Manual", "Manuel")]
    [InlineData("Automatic", "Automatique")]
    [InlineData("DelayedAuto", "Automatique (différé)")]
    [InlineData("Boot", "Démarrage noyau")]
    [InlineData("System", "Système")]
    [InlineData("Weird", "Weird")]   // unknown SCM strings pass through verbatim rather than vanish
    public void Service_StartupLabels(string raw, string label)
    {
        var s = TweakOperationSummary.Describe(Svc("X", raw, raw));
        Assert.Equal($"démarrage → {label}", s.Apply);
        Assert.Equal($"démarrage → {label}", s.Revert);
    }

    [Fact]
    public void Service_NoStartupRevert_DisclosesNonReversible()
    {
        var s = TweakOperationSummary.Describe(Svc("X", "Disabled", revert: null));
        Assert.Equal(TweakOperationSummary.NoRevert, s.Revert);
    }

    // --- Shell ops: the Kind names the interpreter; the raw command is shown verbatim (no target). ---

    [Theory]
    [InlineData(OperationType.PowerShell, "PowerShell")]
    [InlineData(OperationType.Cmd, "Invite de commandes")]
    [InlineData(OperationType.Bcdedit, "Bcdedit")]
    public void Shell_KindAndRawScripts(OperationType type, string kind)
    {
        var s = TweakOperationSummary.Describe(Shell(type, "do-x", "undo-x"));
        Assert.Equal(kind, s.Kind);
        Assert.Equal(string.Empty, s.Target);
        Assert.Equal("do-x", s.Apply);
        Assert.Equal("undo-x", s.Revert);
    }

    [Fact]
    public void Shell_NoRevertScript_DisclosesNonReversible()
    {
        var s = TweakOperationSummary.Describe(Shell(OperationType.PowerShell, "do-x", revert: null));
        Assert.Equal(TweakOperationSummary.NoRevert, s.Revert);
    }

    // --- AppX / ScheduledTask: the engine's verbs, stated plainly. ---

    [Fact]
    public void AppX_DescribesRemoveAndReregister()
    {
        var s = TweakOperationSummary.Describe(new TweakOperation { Type = OperationType.AppX, AppxPackage = "bingnews" });
        Assert.Equal("Application", s.Kind);
        Assert.Equal("bingnews", s.Target);
        Assert.Equal("supprime le paquet", s.Apply);
        Assert.Equal("réenregistre le paquet", s.Revert);
    }

    [Fact]
    public void ScheduledTask_DescribesDisableAndEnable()
    {
        var s = TweakOperationSummary.Describe(new TweakOperation { Type = OperationType.ScheduledTask, TaskPath = "\\Microsoft\\Windows\\Foo" });
        Assert.Equal("Tâche planifiée", s.Kind);
        Assert.Equal("\\Microsoft\\Windows\\Foo", s.Target);
        Assert.Equal("désactive la tâche", s.Apply);
        Assert.Equal("réactive la tâche", s.Revert);
    }

    [Fact]
    public void File_IsRenderedHonestly_WithNoFabricatedEffect()
    {
        // The engine doesn't dispatch File ops, so we must not claim one changes anything.
        var s = TweakOperationSummary.Describe(new TweakOperation { Type = OperationType.File, Path = "C:\\x.txt" });
        Assert.Equal("Fichier", s.Kind);
        Assert.Equal("C:\\x.txt", s.Target);
        Assert.Equal(string.Empty, s.Apply);
        Assert.Equal(TweakOperationSummary.NoRevert, s.Revert);
    }

    // --- Summarize: one row per operation, in order. ---

    [Fact]
    public void Summarize_ReturnsOneRowPerOperation_InOrder()
    {
        var tweak = new Tweak();
        tweak.Operations.Add(Reg("HKCU", "K", "A", "1", "0"));
        tweak.Operations.Add(Svc("S", "Disabled", "Manual"));

        var rows = TweakOperationSummary.Summarize(tweak);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Registre", rows[0].Kind);
        Assert.Equal("Service", rows[1].Kind);
    }

    [Fact]
    public void Summarize_EmptyOperations_IsEmpty()
        => Assert.Empty(TweakOperationSummary.Summarize(new Tweak()));

    // --- SearchTargets: the concrete things a tweak touches, folded into the palette's hidden search keywords so
    // a power user can find a tweak by the registry value or service it changes — never by a target it doesn't. ---

    [Fact]
    public void SearchTargets_IncludesRegistryPathAndServiceName()
    {
        var tweak = new Tweak();
        tweak.Operations.Add(Reg("HKLM", "System\\CurrentControlSet\\Services\\Tcpip\\Parameters", "TcpAckFrequency", "1", null));
        tweak.Operations.Add(Svc("DiagTrack", "Disabled", "Automatic"));

        var targets = TweakOperationSummary.SearchTargets(tweak);

        Assert.Contains("HKLM\\System\\CurrentControlSet\\Services\\Tcpip\\Parameters\\TcpAckFrequency", targets);
        Assert.Contains("DiagTrack", targets);
    }

    [Fact]
    public void SearchTargets_OmitsInlineCommandOps_WhichHaveNoStructuredTarget()
    {
        // A PowerShell/Cmd/Bcdedit op carries its intent in the script text, not a target — SearchTargets must not
        // invent one (the row would claim the tweak "touches" a path it never names).
        var tweak = new Tweak();
        tweak.Operations.Add(Shell(OperationType.PowerShell, "powercfg -h off", "powercfg -h on"));

        Assert.Equal(string.Empty, TweakOperationSummary.SearchTargets(tweak));
    }

    [Fact]
    public void SearchTargets_DeduplicatesRepeatedTargets()
    {
        // Two ops flipping the same service in opposite directions share one target — list it once, not twice.
        var tweak = new Tweak();
        tweak.Operations.Add(Svc("WSearch", "Disabled", "Automatic"));
        tweak.Operations.Add(Svc("WSearch", "Manual", "Automatic"));

        Assert.Equal("WSearch", TweakOperationSummary.SearchTargets(tweak));
    }

    [Fact]
    public void SearchTargets_EmptyOperations_IsEmpty()
        => Assert.Equal(string.Empty, TweakOperationSummary.SearchTargets(new Tweak()));

    // --- Catalog guard: every operation the app actually ships must render a non-blank, populated row. A new
    // op type or a mis-authored operation that produced an empty disclosure would be an invisible "what does
    // this do?" gap in the UI — the honesty mandate forbids it, so we walk the real JSON and assert. ---

    [Fact]
    public void EveryShippedOperation_RendersAPopulatedRow()
    {
        var root = FindRepoDir("src", "AurumTweaks", "Tweaks");
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter() }
        };

        var tweaks = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
            .SelectMany(f => JsonSerializer.Deserialize<List<Tweak>>(File.ReadAllBytes(f), opts) ?? new())
            .ToList();

        Assert.NotEmpty(tweaks);
        foreach (var tweak in tweaks)
        {
            foreach (var row in TweakOperationSummary.Summarize(tweak))
            {
                Assert.False(string.IsNullOrWhiteSpace(row.Kind), $"{tweak.Id}: an operation rendered a blank kind");
                Assert.False(string.IsNullOrWhiteSpace(row.Revert), $"{tweak.Id}: an operation rendered a blank revert");
                // A row with neither a target nor an apply phrase says nothing about what it does.
                Assert.True(!string.IsNullOrWhiteSpace(row.Target) || !string.IsNullOrWhiteSpace(row.Apply),
                    $"{tweak.Id}: an operation rendered a row with no target and no apply text");
            }
        }
    }

    // Walk up from the test's output dir until the source subtree is found — mirrors NavigationCatalogTests so
    // the guard is robust to bin depth and the absence of a working-directory convention.
    private static string FindRepoDir(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException($"Could not locate {string.Join('/', parts)} from {AppContext.BaseDirectory}");
    }
}
