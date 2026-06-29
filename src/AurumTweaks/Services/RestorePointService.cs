using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Win32;
using Serilog;

namespace AurumTweaks.Services;

/// <summary>
/// Creates a Windows System Restore point. Uses PowerShell Checkpoint-Computer under the hood.
/// </summary>
public sealed class RestorePointService : IRestorePointService
{
    public async Task<bool> CreateAsync(string description)
    {
        await EnableSystemRestoreIfDisabledAsync();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -Command \"Checkpoint-Computer -Description '{description.Replace("'", "''")}' -RestorePointType 'MODIFY_SETTINGS'\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch (System.Exception ex)
        {
            Log.Warning(ex, "Failed to create restore point");
            return false;
        }
    }

    public Task<bool> EnableSystemRestoreIfDisabledAsync() => Task.Run(() =>
    {
        try
        {
            // Ensure System Restore is enabled for the system drive.
            var systemDrive = Path.GetPathRoot(System.Environment.SystemDirectory) ?? "C:\\";
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", writable: true);
            key?.SetValue("RPSessionInterval", 1, RegistryValueKind.DWord);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -Command \"Enable-ComputerRestore -Drive '{systemDrive}'\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit();
            return true;
        }
        catch
        {
            return false;
        }
    });
}
