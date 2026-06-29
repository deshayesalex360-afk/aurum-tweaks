using System;
using Microsoft.Win32;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// Pure parsing/comparison for numeric registry values, extracted from <see cref="RegistryService"/> so the
/// round-trip is testable. The honesty point: a DWord/QWord is naturally authored in <b>hex</b> ("0x1") —
/// that's how regedit and every tweak guide show it — and large flags are often written as <b>unsigned</b>
/// decimals ("4294967295"). The old <c>int.Parse(value, NumberStyles.Any)</c> accepted neither (no hex flag;
/// overflow above Int32.MaxValue), so such a tweak would throw inside the writer, return false, and silently
/// do nothing while still being reported as applied. These helpers accept all three forms — decimal-signed,
/// decimal-unsigned, and "0x" hex — and fold them to the same 32/64-bit pattern, so write and read-back agree.
/// </summary>
public static class RegistryValue
{
    public static int ParseDword(string value)
        => TryParseDword(value, out var v) ? v : throw new FormatException($"Invalid DWord registry value: '{value}'.");

    public static long ParseQword(string value)
        => TryParseQword(value, out var v) ? v : throw new FormatException($"Invalid QWord registry value: '{value}'.");

    public static bool TryParseDword(string? value, out int result)
    {
        result = 0;
        var s = value?.Trim();
        if (string.IsNullOrEmpty(s)) return false;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (uint.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var hex))
            { result = unchecked((int)hex); return true; }
            return false;
        }
        if (int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out result))
            return true;
        if (uint.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var u))
        { result = unchecked((int)u); return true; }   // unsigned decimal beyond Int32 (e.g. 4294967295 → -1)
        return false;
    }

    public static bool TryParseQword(string? value, out long result)
    {
        result = 0;
        var s = value?.Trim();
        if (string.IsNullOrEmpty(s)) return false;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (ulong.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var hex))
            { result = unchecked((long)hex); return true; }
            return false;
        }
        if (long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out result))
            return true;
        if (ulong.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var u))
        { result = unchecked((long)u); return true; }
        return false;
    }

    /// <summary>
    /// Does a read-back registry value equal the expected one? DWord/QWord compare numerically (so "0x1" read
    /// back as "1" still matches); everything else is an ordinal string compare. Feeds the IsApplied probe.
    /// </summary>
    public static bool Matches(string? readBack, string? expected, Models.RegistryValueType type)
    {
        if (readBack is null || expected is null) return ReferenceEquals(readBack, expected);
        return type switch
        {
            Models.RegistryValueType.DWord => TryParseDword(readBack, out var a) && TryParseDword(expected, out var b) && a == b,
            Models.RegistryValueType.QWord => TryParseQword(readBack, out var a) && TryParseQword(expected, out var b) && a == b,
            _ => string.Equals(readBack, expected, StringComparison.OrdinalIgnoreCase)
        };
    }
}

/// <summary>
/// Thin wrapper around the Windows Registry API. Hive strings accept "HKLM", "HKCU", "HKCR", "HKU".
/// </summary>
public sealed class RegistryService : IRegistryService
{
    public bool TryReadValue(string hive, string key, string name, out string? current)
    {
        current = null;
        try
        {
            using var root = OpenHive(hive, writable: false);
            using var sub = root?.OpenSubKey(key);
            if (sub is null) return false;
            var v = sub.GetValue(name);
            current = v?.ToString();
            return v != null;
        }
        catch
        {
            return false;
        }
    }

    public bool WriteValue(string hive, string key, string name, string value, Models.RegistryValueType type)
    {
        try
        {
            using var root = OpenHive(hive, writable: true);
            if (root is null) return false;
            using var sub = root.CreateSubKey(key, writable: true);
            if (sub is null) return false;

            var kind = ToRegistryValueKind(type);
            object castValue = kind switch
            {
                RegistryValueKind.DWord => RegistryValue.ParseDword(value),
                RegistryValueKind.QWord => RegistryValue.ParseQword(value),
                RegistryValueKind.Binary => Convert.FromHexString(value),
                _ => value
            };
            sub.SetValue(name, castValue, kind);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool DeleteValue(string hive, string key, string name)
    {
        try
        {
            using var root = OpenHive(hive, writable: true);
            using var sub = root?.OpenSubKey(key, writable: true);
            if (sub is null) return false;
            sub.DeleteValue(name, throwOnMissingValue: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static RegistryKey? OpenHive(string hive, bool writable) => hive.ToUpperInvariant() switch
    {
        "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
        "HKCU" or "HKEY_CURRENT_USER" => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64),
        "HKCR" or "HKEY_CLASSES_ROOT" => RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64),
        "HKU" or "HKEY_USERS" => RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64),
        "HKCC" or "HKEY_CURRENT_CONFIG" => RegistryKey.OpenBaseKey(RegistryHive.CurrentConfig, RegistryView.Registry64),
        _ => null
    };

    private static RegistryValueKind ToRegistryValueKind(Models.RegistryValueType t) => t switch
    {
        Models.RegistryValueType.String => RegistryValueKind.String,
        Models.RegistryValueType.ExpandString => RegistryValueKind.ExpandString,
        Models.RegistryValueType.Binary => RegistryValueKind.Binary,
        Models.RegistryValueType.QWord => RegistryValueKind.QWord,
        Models.RegistryValueType.MultiString => RegistryValueKind.MultiString,
        Models.RegistryValueType.None => RegistryValueKind.None,
        _ => RegistryValueKind.DWord
    };
}
