using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="TweakScriptExportRenderer"/>: the inspectable Tweaks export pack must be rendered from the same
/// <see cref="TweakOperationAction"/> dispatch the engine executes. The apply .reg/.ps1 and revert .reg/.ps1 are
/// therefore value-table tests for reversibility, not a second hand-written guess.
/// </summary>
public class TweakScriptExportRendererTests
{
    [Fact]
    public void Build_RegistryExport_PinsApplyAndRevertRegText()
    {
        var tweak = new Tweak { Id = "reg" };
        tweak.Operations.Add(new TweakOperation
        {
            Type = OperationType.Registry,
            Hive = "HKCU",
            Key = @"Software\Aurum",
            Name = "Flag",
            ValueType = RegistryValueType.DWord,
            Apply = "0x1",
            Revert = null
        });

        var bundle = TweakScriptExportRenderer.Build(new[] { tweak }, "aurum-test");

        Assert.Equal("aurum-test.apply.ps1", bundle.ApplyPowerShellFileName);
        Assert.Equal("aurum-test.revert.ps1", bundle.RevertPowerShellFileName);
        Assert.Equal("aurum-test.apply.reg", bundle.ApplyRegistryFileName);
        Assert.Equal("aurum-test.revert.reg", bundle.RevertRegistryFileName);
        Assert.Equal(1, bundle.RegistryOperationCount);
        Assert.Equal(0, bundle.ScriptOperationCount);
        Assert.Equal(0, bundle.IrreversibleOperationCount);
        Assert.Contains("""('import "' + $AurumRegistryFile + '" /reg:64')""", bundle.ApplyPowerShell);

        AssertText("""
Windows Registry Editor Version 5.00

; Aurum Tweaks - application
; Importé par aurum-test.apply.ps1 ou inspecté manuellement.
; Limite: l'import exige les droits administrateur et peut être refusé par Windows/politique.

; Tweak reg
[HKEY_CURRENT_USER\Software\Aurum]
"Flag"=dword:00000001



""", bundle.ApplyRegistry);

        AssertText("""
Windows Registry Editor Version 5.00

; Aurum Tweaks - restauration
; Importé par aurum-test.revert.ps1 ou inspecté manuellement.
; Limite: l'import exige les droits administrateur et peut être refusé par Windows/politique.

; Tweak reg
[HKEY_CURRENT_USER\Software\Aurum]
"Flag"=-



""", bundle.RevertRegistry);
    }

    [Fact]
    public void Build_ServiceAndCmdExport_PinsApplyScript_AndRevertUsesTheInverseActions()
    {
        var tweak = new Tweak { Id = "mixed" };
        tweak.Operations.Add(new TweakOperation
        {
            Type = OperationType.Service,
            ServiceName = "DiagTrack",
            StartupApply = "Disabled",
            StartupRevert = "Automatic"
        });
        tweak.Operations.Add(new TweakOperation
        {
            Type = OperationType.Cmd,
            Script = "ipconfig /flushdns",
            RevertScript = "echo undo"
        });

        var bundle = TweakScriptExportRenderer.Build(new[] { tweak }, "mix");

        Assert.Equal(0, bundle.RegistryOperationCount);
        Assert.Equal(2, bundle.ScriptOperationCount);
        AssertText("""
# Aurum Tweaks - script de application
# Inspectable: généré depuis la sélection; rien n'a été exécuté pendant l'export.
# Limite: les droits administrateur et les protections Windows peuvent refuser une étape.
# Registre: ce script force la vue 64 bits, comme l'application Aurum Tweaks x64.
$ErrorActionPreference = 'Stop'
$AurumFailures = 0

function Invoke-AurumStep {
    param([string]$Label, [scriptblock]$Step)
    try { & $Step }
    catch {
        $script:AurumFailures++
        Write-Error -ErrorAction Continue ($Label + ' : ' + $_.Exception.Message)
    }
}

function Invoke-AurumProcess {
    param([string]$Label, [string]$FileName, [string]$Arguments)
    Invoke-AurumStep $Label {
        $p = Start-Process -FilePath $FileName -ArgumentList $Arguments -Wait -PassThru -NoNewWindow
        if ($null -eq $p) { throw 'Processus non démarré.' }
        if ($p.ExitCode -ne 0) { throw ('Code de sortie ' + $p.ExitCode + '.') }
    }
}

function Set-AurumServiceStartup {
    param([string]$ServiceName, [string]$StartupType)
    $root = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    $key = $root.OpenSubKey('SYSTEM\CurrentControlSet\Services\' + $ServiceName, $true)
    if ($null -eq $key) { $root.Dispose(); throw ('Service introuvable: ' + $ServiceName) }
    $start = switch ($StartupType.ToLowerInvariant()) {
        'boot' { 0; break }
        'system' { 1; break }
        'automatic' { 2; break }
        'auto' { 2; break }
        'delayedauto' { 2; break }
        'manual' { 3; break }
        'disabled' { 4; break }
        default { 3; break }
    }
    $delayed = if ($StartupType.Equals('DelayedAuto', [StringComparison]::OrdinalIgnoreCase)) { 1 } else { 0 }
    try {
        $key.SetValue('Start', [int]$start, [Microsoft.Win32.RegistryValueKind]::DWord)
        $key.SetValue('DelayedAutostart', [int]$delayed, [Microsoft.Win32.RegistryValueKind]::DWord)
    }
    finally { $key.Dispose(); $root.Dispose() }
}

# Aucun fichier .reg compagnon à importer pour cette sélection.

# Tweak mixed
Invoke-AurumStep 'mixed: Service application' {
    Set-AurumServiceStartup 'DiagTrack' 'Disabled'
}

Invoke-AurumProcess 'mixed: Cmd application' 'cmd.exe' '/c ipconfig /flushdns'


if ($AurumFailures -gt 0) { throw ($AurumFailures.ToString() + ' opération(s) ont échoué.') }

""", bundle.ApplyPowerShell);

        Assert.Contains("Set-AurumServiceStartup 'DiagTrack' 'Automatic'", bundle.RevertPowerShell);
        Assert.Contains("Invoke-AurumProcess 'mixed: Cmd restauration' 'cmd.exe' '/c echo undo'", bundle.RevertPowerShell);
        Assert.DoesNotContain("/c ipconfig /flushdns", bundle.RevertPowerShell);
    }

    [Fact]
    public void Build_ShellWithoutRevert_MarksTheRevertScriptAsFailingInsteadOfSuccessful()
    {
        var tweak = new Tweak { Id = "no-undo" };
        tweak.Operations.Add(new TweakOperation
        {
            Type = OperationType.PowerShell,
            Script = "Set-Foo -On",
            RevertScript = null
        });

        var bundle = TweakScriptExportRenderer.Build(new[] { tweak }, "undo");

        Assert.Equal(1, bundle.IrreversibleOperationCount);
        Assert.Contains("Invoke-AurumProcess 'no-undo: PowerShell application' 'powershell.exe'", bundle.ApplyPowerShell);
        Assert.Contains("Set-Foo -On", bundle.ApplyPowerShell);
        Assert.Contains("throw 'aucun rétablissement automatique'", bundle.RevertPowerShell);
    }

    private static void AssertText(string expected, string actual) =>
        Assert.Equal(expected.ReplaceLineEndings(), actual.ReplaceLineEndings());
}
