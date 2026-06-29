using System.Collections.Generic;
using System.Linq;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the medium/health/bus token mappings. The load-bearing point: each <c>FromToken</c> accepts <b>both</b> the
/// numeric code and the friendly enum name, because which one Get-PhysicalDisk serialises varies by Windows / Storage
/// module version — betting on one form would silently mislabel a drive on the other.
/// </summary>
public class DriveTokenMappingTests
{
    [Theory]
    [InlineData("SSD", DriveMedia.Ssd)]
    [InlineData("ssd", DriveMedia.Ssd)]
    [InlineData("4", DriveMedia.Ssd)]
    [InlineData("HDD", DriveMedia.Hdd)]
    [InlineData("3", DriveMedia.Hdd)]
    [InlineData("SCM", DriveMedia.Scm)]
    [InlineData("5", DriveMedia.Scm)]
    [InlineData("0", DriveMedia.Unknown)]
    [InlineData("Unspecified", DriveMedia.Unknown)]
    [InlineData("", DriveMedia.Unknown)]
    [InlineData(null, DriveMedia.Unknown)]
    public void Media_FromToken_AcceptsBothNumericAndFriendly(string? token, DriveMedia expected)
        => Assert.Equal(expected, DriveMediaCatalog.FromToken(token));

    [Theory]
    [InlineData(DriveMedia.Ssd, "SSD")]
    [InlineData(DriveMedia.Hdd, "Disque dur (HDD)")]
    [InlineData(DriveMedia.Scm, "Mémoire persistante (SCM)")]
    [InlineData(DriveMedia.Unknown, "Type inconnu")]
    public void Media_Label_IsFrench(DriveMedia media, string expected)
        => Assert.Equal(expected, DriveMediaCatalog.Label(media));

    [Theory]
    [InlineData("Healthy", DriveHealth.Healthy)]
    [InlineData("0", DriveHealth.Healthy)]
    [InlineData("Warning", DriveHealth.Warning)]
    [InlineData("1", DriveHealth.Warning)]
    [InlineData("Unhealthy", DriveHealth.Unhealthy)]
    [InlineData("2", DriveHealth.Unhealthy)]
    [InlineData("5", DriveHealth.Unknown)]
    [InlineData("", DriveHealth.Unknown)]
    [InlineData(null, DriveHealth.Unknown)]
    public void Health_FromToken_AcceptsBothNumericAndFriendly(string? token, DriveHealth expected)
        => Assert.Equal(expected, DriveHealthCatalog.FromToken(token));

    [Theory]
    [InlineData("7", "USB")]
    [InlineData("11", "SATA")]
    [InlineData("17", "NVMe")]
    [InlineData("NVMe", "NVMe")]   // already friendly — passed through verbatim
    [InlineData("SATA", "SATA")]
    [InlineData("99", "99")]       // uncommon code we don't name — never invent one
    [InlineData("", "Bus inconnu")]
    [InlineData(null, "Bus inconnu")]
    public void Bus_Describe_NamesCommonAndPassesThroughRest(string? token, string expected)
        => Assert.Equal(expected, DriveBus.Describe(token));

    [Theory]
    [InlineData("7", true)]
    [InlineData("USB", true)]
    [InlineData("usb", true)]
    [InlineData("11", false)]
    [InlineData("NVMe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Bus_IsUsb_DetectsBridgedDrives(string? token, bool expected)
        => Assert.Equal(expected, DriveBus.IsUsb(token));
}

/// <summary>Pins the metric formatters — fixed fr-FR (comma decimal), and an absent value renders « — » so a counter
/// Windows didn't report never looks like a real zero.</summary>
public class DriveHealthFormatTests
{
    [Theory]
    [InlineData(42, "42 °C")]
    [InlineData(0, "0 °C")]
    [InlineData(null, "—")]
    public void Temperature_FormatsOrDashes(int? c, string expected)
        => Assert.Equal(expected, DriveHealthFormat.Temperature(c));

    [Theory]
    [InlineData(2, "2 %")]
    [InlineData(100, "100 %")]
    [InlineData(null, "—")]
    public void Wear_FormatsOrDashes(int? p, string expected)
        => Assert.Equal(expected, DriveHealthFormat.Wear(p));

    [Theory]
    [InlineData(null, "—")]
    [InlineData(-1L, "—")]                       // defensive: a negative count is nonsense → dash, never shown
    [InlineData(500L, "500 h")]                  // under a year — no approximation
    [InlineData(8766L, "8766 h (≈ 1 an)")]       // exactly one year (365.25 d) — boundary
    [InlineData(13149L, "13149 h (≈ 1,5 an)")]   // fr-FR comma decimal, singular « an » below 2
    [InlineData(17532L, "17532 h (≈ 2 ans)")]    // plural « ans »
    public void PowerOnHours_AddsYearApproxOverAYear(long? hours, string expected)
        => Assert.Equal(expected, DriveHealthFormat.PowerOnHours(hours));
}

/// <summary>
/// Pins the load-bearing honesty core. Windows' own health is authoritative and outranks everything; our derived
/// signals (high wear, past uncorrected errors) may only escalate a healthy/unknown drive as far as « à surveiller »,
/// never fabricate a « failing » verdict. Errors-first ordering, exactly like the memory/CPU stability verdict.
/// </summary>
public class DriveHealthVerdictTests
{
    private static DriveVerdict V(DriveHealth h, int? wear = null, long? unc = null)
        => DriveHealthVerdict.Evaluate(h, wear, unc).Verdict;

    [Fact] public void WindowsUnhealthy_IsCritical() => Assert.Equal(DriveVerdict.Critical, V(DriveHealth.Unhealthy));

    [Fact]
    public void WindowsUnhealthy_OutranksEverythingElse()   // health is authoritative — wear/errors can't soften it
        => Assert.Equal(DriveVerdict.Critical, V(DriveHealth.Unhealthy, wear: 1, unc: 0));

    [Fact] public void WindowsWarning_IsWatch() => Assert.Equal(DriveVerdict.Watch, V(DriveHealth.Warning));

    [Fact]
    public void HighWear_EscalatesHealthyToWatch_AtThreshold()
        => Assert.Equal(DriveVerdict.Watch, V(DriveHealth.Healthy, wear: DriveHealthVerdict.WearWatchThreshold));

    [Fact]
    public void WearBelowThreshold_StaysHealthy()
        => Assert.Equal(DriveVerdict.Healthy, V(DriveHealth.Healthy, wear: DriveHealthVerdict.WearWatchThreshold - 1));

    [Fact]
    public void UncorrectedErrors_EscalateHealthyToWatch()
        => Assert.Equal(DriveVerdict.Watch, V(DriveHealth.Healthy, unc: 1));

    [Fact]
    public void ZeroErrors_AreNotAnEscalator()
        => Assert.Equal(DriveVerdict.Healthy, V(DriveHealth.Healthy, unc: 0));

    [Fact] public void HealthyAndClean_IsHealthy() => Assert.Equal(DriveVerdict.Healthy, V(DriveHealth.Healthy));

    [Fact] public void NoData_IsUnknown() => Assert.Equal(DriveVerdict.Unknown, V(DriveHealth.Unknown));

    [Fact]
    public void DerivedSignals_EscalateEvenWhenHealthIsUnknown()   // we still warn on a worn/erroring drive Windows can't grade
    {
        Assert.Equal(DriveVerdict.Watch, V(DriveHealth.Unknown, wear: 90));
        Assert.Equal(DriveVerdict.Watch, V(DriveHealth.Unknown, unc: 3));
    }

    [Fact]
    public void Message_FollowsThePrecedence()
    {
        // Windows warning wins its message even with wear+errors also tripped.
        Assert.Contains("avertissement", DriveHealthVerdict.Evaluate(DriveHealth.Warning, 95, 5).Message);
        // Among derived signals, wear is reported before errors.
        Assert.Contains("Usure", DriveHealthVerdict.Evaluate(DriveHealth.Healthy, 95, 5).Message);
    }
}

/// <summary>
/// Pins the parser of the flat <c>Get-PhysicalDisk | Get-StorageReliabilityCounter</c> CSV. The honesty points:
/// header-keyed (a column re-order can't shift data into the wrong field), empty reliability cells stay <c>null</c>
/// (« unknown », never a fabricated 0), and the ConvertTo-Csv quoting (every field quoted, embedded commas) is decoded
/// via the shared <see cref="CsvRow"/> splitter.
/// </summary>
public class DrivePhysicalParserTests
{
    private static readonly string[] Columns =
    {
        "FriendlyName", "MediaType", "HealthStatus", "BusType", "Size",
        "Temperature", "Wear", "PowerOnHours", "ReadErrorsUncorrected", "WriteErrorsUncorrected"
    };

    // PowerShell 5.1 ConvertTo-Csv quotes every field; mimic that exactly.
    private static string Row(params string[] fields)
        => string.Join(",", fields.Select(f => "\"" + f.Replace("\"", "\"\"") + "\""));

    private static string Csv(params string[] dataRows)
        => Row(Columns) + "\r\n" + string.Join("\r\n", dataRows) + "\r\n";

    [Fact]
    public void Parse_ReadsAHealthySsdWithFullCounters()
    {
        var csv = Csv(Row("Samsung SSD 980 PRO 1TB", "SSD", "Healthy", "NVMe", "1000204886016",
                          "41", "2", "8760", "0", "0"));
        var d = Assert.Single(DrivePhysicalParser.Parse(csv));

        Assert.Equal("Samsung SSD 980 PRO 1TB", d.Name);
        Assert.Equal(DriveMedia.Ssd, d.Media);
        Assert.Equal(DriveHealth.Healthy, d.Health);
        Assert.Equal("NVMe", d.BusDisplay);
        Assert.False(d.IsUsb);
        Assert.Equal(1000204886016, d.SizeBytes);
        Assert.Equal(41, d.Temperature);
        Assert.Equal(2, d.WearPercent);
        Assert.Equal(8760, d.PowerOnHours);
        Assert.Equal(0, d.UncorrectedErrors);          // 0 read + 0 write — present and genuinely zero
        Assert.Equal(DriveVerdict.Healthy, d.Verdict);
    }

    [Fact]
    public void Parse_KeepsAbsentReliabilityCountersAsUnknown_NotZero()
    {
        // A USB-bridged HDD given in numeric token form, with every reliability cell empty.
        var csv = Csv(Row("WD Elements 25A3 USB Device", "3", "0", "7", "2000398934016",
                          "", "", "", "", ""));
        var d = Assert.Single(DrivePhysicalParser.Parse(csv));

        Assert.Equal(DriveMedia.Hdd, d.Media);          // from "3"
        Assert.Equal(DriveHealth.Healthy, d.Health);    // from "0"
        Assert.Equal("USB", d.BusDisplay);              // from "7"
        Assert.True(d.IsUsb);
        Assert.Null(d.Temperature);
        Assert.Null(d.WearPercent);
        Assert.Null(d.PowerOnHours);
        Assert.Null(d.UncorrectedErrors);               // both cells empty → unknown, not 0
        Assert.False(d.HasTemperature);
        Assert.Equal("—", d.UncorrectedErrorsDisplay);
        Assert.Equal("—", d.TemperatureDisplay);
    }

    [Theory]
    [InlineData("2", "3", 5L)]      // both present → summed
    [InlineData("4", "", 4L)]       // only read present → read + 0
    [InlineData("", "6", 6L)]       // only write present → 0 + write
    public void Parse_UncorrectedErrors_SumWhenEitherPresent(string read, string write, long expected)
    {
        var csv = Csv(Row("Disk", "SSD", "Healthy", "SATA", "500107862016", "30", "1", "100", read, write));
        var d = Assert.Single(DrivePhysicalParser.Parse(csv));
        Assert.Equal(expected, d.UncorrectedErrors);
    }

    [Fact]
    public void Parse_IsHeaderKeyed_NotPositional()
    {
        // Columns deliberately shuffled — values must still land in the right fields.
        var header = Row("Size", "FriendlyName", "HealthStatus", "MediaType", "BusType",
                         "Wear", "Temperature", "WriteErrorsUncorrected", "ReadErrorsUncorrected", "PowerOnHours");
        var data = Row("500107862016", "Crucial MX500", "Healthy", "SSD", "SATA",
                       "7", "35", "0", "0", "4380");
        var d = Assert.Single(DrivePhysicalParser.Parse(header + "\r\n" + data));

        Assert.Equal("Crucial MX500", d.Name);
        Assert.Equal(DriveMedia.Ssd, d.Media);
        Assert.Equal(500107862016, d.SizeBytes);
        Assert.Equal(35, d.Temperature);
        Assert.Equal(7, d.WearPercent);
        Assert.Equal(4380, d.PowerOnHours);
    }

    [Fact]
    public void Parse_PreservesCommaInsideAQuotedName()
    {
        var csv = Csv(Row("Crucial CT500, MX500 SSD", "SSD", "Healthy", "SATA", "500107862016",
                          "30", "1", "100", "0", "0"));
        var d = Assert.Single(DrivePhysicalParser.Parse(csv));
        Assert.Equal("Crucial CT500, MX500 SSD", d.Name);   // comma is data, not a field break
    }

    [Fact]
    public void Parse_EmptyOrHeaderOnly_YieldsNoDrives()
    {
        Assert.Empty(DrivePhysicalParser.Parse(null));
        Assert.Empty(DrivePhysicalParser.Parse("   "));
        Assert.Empty(DrivePhysicalParser.Parse(Row(Columns) + "\r\n"));   // header but no data rows
    }

    [Fact]
    public void Parse_SkipsARowWithNoIdentity()
    {
        var csv = Csv(Row("", "", "", "", "", "", "", "", "", ""));       // name, media, size all blank
        Assert.Empty(DrivePhysicalParser.Parse(csv));
    }

    [Fact]
    public void Parse_ReadsMultipleDrives()
    {
        var csv = Csv(
            Row("Disk A", "SSD", "Healthy", "NVMe", "1000204886016", "40", "3", "8760", "0", "0"),
            Row("Disk B", "HDD", "Healthy", "SATA", "4000787030016", "33", "", "26280", "0", "0"));
        Assert.Equal(2, DrivePhysicalParser.Parse(csv).Count);
    }
}

/// <summary>Pins the report aggregation: honest counts and a « worst drive » headline with explicit
/// Critical &gt; Watch &gt; Unknown &gt; Healthy precedence (so an Unknown drive never reads as « better » than Healthy).</summary>
public class DriveHealthReportTests
{
    private static DriveHealthInfo Drive(DriveHealth h, int? wear = null, long? unc = null)
        => new("Disk", DriveMedia.Ssd, h, "SATA", 500107862016, Temperature: 30, WearPercent: wear,
               PowerOnHours: 100, UncorrectedErrors: unc);

    private static DriveHealthReport Report(params DriveHealthInfo[] drives)
        => new(drives, QueryOk: true);

    [Fact]
    public void Counts_TallyByVerdict()
    {
        var report = Report(
            Drive(DriveHealth.Healthy),
            Drive(DriveHealth.Healthy, wear: 90),    // Watch
            Drive(DriveHealth.Unhealthy));           // Critical
        Assert.Equal(3, report.Count);
        Assert.Equal(1, report.HealthyCount);
        Assert.Equal(1, report.WatchCount);
        Assert.Equal(1, report.CriticalCount);
    }

    [Fact]
    public void Worst_IsCriticalWhenAnyDriveIsCritical()
        => Assert.Equal(DriveVerdict.Critical,
                        Report(Drive(DriveHealth.Healthy), Drive(DriveHealth.Unhealthy)).Worst);

    [Fact]
    public void Worst_IsWatchWhenWatchButNoCritical()
        => Assert.Equal(DriveVerdict.Watch,
                        Report(Drive(DriveHealth.Healthy), Drive(DriveHealth.Healthy, wear: 90)).Worst);

    [Fact]
    public void Worst_PrefersUnknownOverHealthy()   // a drive we can't grade isn't « better » than a healthy one
        => Assert.Equal(DriveVerdict.Unknown,
                        Report(Drive(DriveHealth.Healthy), Drive(DriveHealth.Unknown)).Worst);

    [Fact]
    public void Worst_IsHealthyWhenAllHealthy()
        => Assert.Equal(DriveVerdict.Healthy, Report(Drive(DriveHealth.Healthy), Drive(DriveHealth.Healthy)).Worst);

    [Fact]
    public void Worst_OfNoDrives_IsUnknown()
        => Assert.Equal(DriveVerdict.Unknown, new DriveHealthReport(new List<DriveHealthInfo>(), QueryOk: false).Worst);
}

/// <summary>Pins the per-drive view-model surface: capacity reuses the shared <see cref="ByteSize"/> formatter, the
/// name has an honest fallback, and the Has*/Show* flags match the data so the UI never shows a chip for an absent
/// metric nor two verdict badges at once.</summary>
public class DriveHealthInfoTests
{
    private static DriveHealthInfo Make(long size = 1073741824, int? temp = 40, int? wear = 5,
        long? hours = 100, long? unc = 0, DriveHealth health = DriveHealth.Healthy, string name = "Disk")
        => new(name, DriveMedia.Ssd, health, "SATA", size, temp, wear, hours, unc);

    [Fact]
    public void SizeDisplay_ReusesByteSize()
        => Assert.Equal(ByteSize.Format(1073741824), Make(size: 1073741824).SizeDisplay);

    [Theory]
    [InlineData("", "Disque")]
    [InlineData("   ", "Disque")]
    [InlineData("  Samsung  ", "Samsung")]
    public void Name_FallsBackAndTrims(string raw, string expected)
        => Assert.Equal(expected, Make(name: raw).Name);

    [Fact]
    public void HasFlags_FollowNullability()
    {
        var full = Make(temp: 40, wear: 5, hours: 100, unc: 0);
        Assert.True(full.HasTemperature);
        Assert.True(full.HasWear);
        Assert.True(full.HasPowerOnHours);
        Assert.True(full.HasErrors);

        var bare = Make(temp: null, wear: null, hours: null, unc: null);
        Assert.False(bare.HasTemperature);
        Assert.False(bare.HasWear);
        Assert.False(bare.HasPowerOnHours);
        Assert.False(bare.HasErrors);
    }

    [Fact]
    public void HasPowerOnHours_IsFalseForNegative()   // defensive: a bogus negative count isn't a real metric
        => Assert.False(Make(hours: -5).HasPowerOnHours);

    [Fact]
    public void ShowFlags_AreExactlyOnePerVerdict()
    {
        AssertOneBadge(Make(health: DriveHealth.Unhealthy), critical: true);
        AssertOneBadge(Make(health: DriveHealth.Warning), watch: true);
        AssertOneBadge(Make(health: DriveHealth.Healthy, wear: 0, unc: 0), healthy: true);
        AssertOneBadge(Make(health: DriveHealth.Unknown, temp: null, wear: null, hours: null, unc: null), unknown: true);
    }

    private static void AssertOneBadge(DriveHealthInfo d,
        bool critical = false, bool watch = false, bool healthy = false, bool unknown = false)
    {
        Assert.Equal(critical, d.ShowCritical);
        Assert.Equal(watch, d.ShowWatch);
        Assert.Equal(healthy, d.ShowHealthy);
        Assert.Equal(unknown, d.ShowUnknown);
        // exactly one badge visible
        Assert.Equal(1, new[] { d.ShowCritical, d.ShowWatch, d.ShowHealthy, d.ShowUnknown }.Count(b => b));
    }
}
