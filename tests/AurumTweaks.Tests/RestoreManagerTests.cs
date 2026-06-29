using System;
using System.Threading.Tasks;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

public class WmiDateParserTests
{
    [Fact]
    public void Parse_ValidStamp_ZeroOffset_ReadsAllComponents()
    {
        Assert.True(WmiDateParser.TryParse("20260613143000.000000-000", out var dt));
        Assert.Equal(2026, dt.Year);
        Assert.Equal(6, dt.Month);
        Assert.Equal(13, dt.Day);
        Assert.Equal(14, dt.Hour);
        Assert.Equal(30, dt.Minute);
        Assert.Equal(0, dt.Second);
        Assert.Equal(TimeSpan.Zero, dt.Offset);
    }

    [Fact]
    public void Parse_PositiveOffset_AppliesSign()
    {
        Assert.True(WmiDateParser.TryParse("20260613143000.000000+060", out var dt));
        Assert.Equal(TimeSpan.FromMinutes(60), dt.Offset);
    }

    [Fact]
    public void Parse_NegativeOffset_AppliesSign()
    {
        Assert.True(WmiDateParser.TryParse("20260613143000.000000-300", out var dt));
        Assert.Equal(TimeSpan.FromMinutes(-300), dt.Offset);
    }

    /// <summary>Honesty rule: any malformation must return false so the caller shows « — », never a fabricated date.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("2026061314300")]                 // too short (< 25)
    [InlineData("20260613143000.000000X000")]     // bad sign char at index 21
    [InlineData("20260613143000.000000-0X0")]     // non-numeric offset
    [InlineData("20261313143000.000000-000")]     // month 13 → out of range
    [InlineData("20260632143000.000000-000")]     // day 32 → out of range
    [InlineData("20X60613143000.000000-000")]     // non-numeric year
    public void Parse_Malformed_ReturnsFalse(string? wmi)
        => Assert.False(WmiDateParser.TryParse(wmi, out _));
}

public class RestorePointTypeInfoTests
{
    [Theory]
    [InlineData(0, "Installation d'application")]
    [InlineData(1, "Désinstallation d'application")]
    [InlineData(10, "Installation de pilote")]
    [InlineData(12, "Modification du système")]
    [InlineData(13, "Opération annulée")]
    [InlineData(-1, "Point de restauration")]     // unknown → neutral label, never a guessed cause
    [InlineData(999, "Point de restauration")]
    public void Label_MapsKnownCodes_AndFallsBack(int type, string expected)
        => Assert.Equal(expected, RestorePointTypeInfo.Label(type));
}

public class RestorePointDisplayTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 6, 13, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Dated_FormatsWhenAndAge_AndSummary()
    {
        var p = new RestorePoint(42, "Avant maj", 12, Stamp);
        Assert.True(p.HasDate);
        Assert.Equal("13/06/2026 14:30", p.WhenDisplay);   // the stamp's own wall clock, deterministic
        Assert.NotEqual(string.Empty, p.AgeDisplay);
        Assert.Equal("Avant maj", p.DescriptionDisplay);
        Assert.Equal("Modification du système", p.TypeLabel);
        Assert.Equal("#42 · Modification du système", p.SummaryDisplay);
    }

    [Fact]
    public void Undated_ShowsDash_AndEmptyAge_NeverFabricatesDate()
    {
        var p = new RestorePoint(7, "x", 0, null);
        Assert.False(p.HasDate);
        Assert.Equal("—", p.WhenDisplay);
        Assert.Equal(string.Empty, p.AgeDisplay);
    }

    [Theory]
    [InlineData("", "(sans description)")]
    [InlineData("   ", "(sans description)")]
    [InlineData("  Mon point  ", "Mon point")]
    public void DescriptionDisplay_BlankBecomesPlaceholder_ElseTrimmed(string desc, string expected)
        => Assert.Equal(expected, new RestorePoint(1, desc, 0, null).DescriptionDisplay);
}

public class RestorePointCsvParserTests
{
    private const string Header = "\"SequenceNumber\",\"Description\",\"RestorePointType\",\"CreationTime\"";

    [Fact]
    public void Parse_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(RestorePointCsvParser.Parse(null));
        Assert.Empty(RestorePointCsvParser.Parse(""));
    }

    [Fact]
    public void Parse_TwoRows_NewestFirst_ReadsFields()
    {
        var csv = Header + "\r\n" +
                  "\"42\",\"Avant maj pilote\",\"10\",\"20260613143000.000000-000\"\r\n" +
                  "\"43\",\"Point Aurum\",\"12\",\"20260614090000.000000-000\"\r\n";

        var points = RestorePointCsvParser.Parse(csv);

        Assert.Equal(2, points.Count);
        Assert.Equal(43, points[0].SequenceNumber);   // sequence numbers increase → descending = newest first
        Assert.Equal(42, points[1].SequenceNumber);
        Assert.Equal("Avant maj pilote", points[1].Description);
        Assert.Equal(10, points[1].TypeRaw);
        Assert.True(points[1].HasDate);
    }

    /// <summary>Header-keyed (not positional): a reordered column set must still map correctly.</summary>
    [Fact]
    public void Parse_HeaderKeyed_ToleratesColumnReorder()
    {
        var csv = "\"CreationTime\",\"RestorePointType\",\"SequenceNumber\",\"Description\"\r\n" +
                  "\"20260613143000.000000-000\",\"12\",\"55\",\"Réordonné\"";

        var p = Assert.Single(RestorePointCsvParser.Parse(csv));
        Assert.Equal(55, p.SequenceNumber);
        Assert.Equal("Réordonné", p.Description);
        Assert.Equal(12, p.TypeRaw);
        Assert.True(p.HasDate);
    }

    [Fact]
    public void Parse_RowWithoutSequenceNumber_IsDropped()
    {
        var csv = Header + "\r\n" +
                  "\"\",\"no seq\",\"12\",\"20260613143000.000000-000\"\r\n" +
                  "\"9\",\"ok\",\"12\",\"20260613143000.000000-000\"";

        var p = Assert.Single(RestorePointCsvParser.Parse(csv));
        Assert.Equal(9, p.SequenceNumber);
    }

    [Fact]
    public void Parse_UnknownType_BecomesNeutralLabel()
    {
        var csv = Header + "\r\n\"3\",\"weird\",\"\",\"20260613143000.000000-000\"";

        var p = Assert.Single(RestorePointCsvParser.Parse(csv));
        Assert.Equal(-1, p.TypeRaw);
        Assert.Equal("Point de restauration", p.TypeLabel);
    }

    [Fact]
    public void Parse_MalformedDate_LeavesPointUndated_NeverFabricates()
    {
        var csv = Header + "\r\n\"3\",\"bad date\",\"12\",\"not-a-date\"";

        var p = Assert.Single(RestorePointCsvParser.Parse(csv));
        Assert.False(p.HasDate);
        Assert.Equal("—", p.WhenDisplay);
    }

    /// <summary>A stray #TYPE comment line (older PowerShell) must be skipped, not mistaken for the header.</summary>
    [Fact]
    public void Parse_SkipsTypeCommentLine()
    {
        var csv = "#TYPE Selected.Microsoft.PowerShell\r\n" + Header +
                  "\r\n\"1\",\"x\",\"12\",\"20260613143000.000000-000\"";

        var p = Assert.Single(RestorePointCsvParser.Parse(csv));
        Assert.Equal(1, p.SequenceNumber);
    }
}

public class RestoreFrequencyStateTests
{
    [Fact]
    public void Absent_IsDefault_OffersOnlyUnthrottle()
    {
        var s = new RestoreFrequencyState(null, false);
        Assert.True(s.IsDefault);
        Assert.False(s.IsUnthrottled);
        Assert.False(s.IsCustom);
        Assert.True(s.CanUnthrottle);
        Assert.False(s.CanRestoreThrottle);   // nothing present to reset → no dead button
        Assert.Contains("défaut", s.StateDisplay);
    }

    [Fact]
    public void Zero_IsUnthrottled_OffersOnlyRestore()
    {
        var s = new RestoreFrequencyState("0", true);
        Assert.True(s.IsUnthrottled);
        Assert.False(s.IsDefault);
        Assert.False(s.CanUnthrottle);
        Assert.True(s.CanRestoreThrottle);
        Assert.Contains("Sans limite", s.StateDisplay);
    }

    [Fact]
    public void Present1440_IsDefault_OffersOnlyUnthrottle()
    {
        var s = new RestoreFrequencyState("1440", true);
        Assert.True(s.IsDefault);
        Assert.False(s.IsUnthrottled);
        Assert.True(s.CanUnthrottle);
        Assert.False(s.CanRestoreThrottle);   // already default → reset would be a no-op
    }

    [Fact]
    public void Custom_IsNeither_OffersBoth()
    {
        var s = new RestoreFrequencyState("60", true);
        Assert.True(s.IsCustom);
        Assert.False(s.IsDefault);
        Assert.False(s.IsUnthrottled);
        Assert.True(s.CanUnthrottle);
        Assert.True(s.CanRestoreThrottle);
        Assert.Contains("60", s.StateDisplay);
    }

    [Theory]
    [InlineData("0x0")]   // hex zero == 0 numerically → still « sans limite »
    public void Zero_ComparesNumerically(string live)
        => Assert.True(new RestoreFrequencyState(live, true).IsUnthrottled);
}

public class CheckpointOutcomeTests
{
    [Fact]
    public void Failed_IsNotInvoked_HasFailureHeadline()
    {
        var o = CheckpointOutcome.Failed;
        Assert.False(o.Invoked);
        Assert.False(o.Created);
        Assert.False(o.Throttled);
        Assert.False(o.Unmeasured);
        Assert.Contains("Échec", o.Headline);
    }

    [Fact]
    public void Created_WhenCountRose()
    {
        var o = new CheckpointOutcome(true, true, 3, 4);
        Assert.True(o.Created);
        Assert.False(o.Throttled);
        Assert.Contains("Point de restauration créé", o.Headline);
    }

    /// <summary>The honesty case: Checkpoint-Computer ran but the count didn't rise → Windows skipped it under the 24h throttle, never a fake « créé ».</summary>
    [Fact]
    public void Throttled_WhenCountDidNotRise()
    {
        var o = new CheckpointOutcome(true, true, 4, 4);
        Assert.False(o.Created);
        Assert.True(o.Throttled);
        Assert.Contains("24 h", o.Headline);
    }

    [Fact]
    public void Unmeasured_WhenInvokedButNotMeasured()
    {
        var o = new CheckpointOutcome(true, false, 0, 0);
        Assert.True(o.Unmeasured);
        Assert.False(o.Created);
        Assert.False(o.Throttled);
        Assert.Contains("n'a pas pu être vérifié", o.Headline);
    }
}

public class RestoreOverviewTests
{
    private static readonly RestoreFrequencyState Freq = new(null, false);
    private static RestorePoint P(int seq, DateTimeOffset? at) => new(seq, "d", 12, at);

    [Fact]
    public void Failed_NotQueryOk_HasReadImpossibleHeadline()
    {
        var o = RestoreOverview.Failed(Freq);
        Assert.False(o.QueryOk);
        Assert.Empty(o.Points);
        Assert.Contains("Lecture impossible", o.Headline);
    }

    [Fact]
    public void QueryOk_NoPoints_SaysNone()
    {
        var o = new RestoreOverview(true, Array.Empty<RestorePoint>(), Freq);
        Assert.False(o.HasPoints);
        Assert.Null(o.Latest);
        Assert.Contains("Aucun point", o.Headline);
    }

    [Fact]
    public void QueryOk_WithPoints_ReportsCountAndLatest()
    {
        var stamp = new DateTimeOffset(2026, 6, 13, 14, 30, 0, TimeSpan.Zero);
        var o = new RestoreOverview(true, new[] { P(43, stamp), P(42, stamp) }, Freq);
        Assert.True(o.HasPoints);
        Assert.Equal(2, o.Count);
        Assert.Equal(43, o.Latest!.SequenceNumber);   // Points[0] (caller hands them newest-first)
        Assert.Contains("2 point(s)", o.Headline);
        Assert.Contains("13/06/2026 14:30", o.Headline);
    }
}

public class RestoreManagerServiceTests
{
    private const string SrKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    private const string FreqValue = "SystemRestorePointCreationFrequency";

    private static (RestoreManagerService svc, FakeRegistryService reg) New()
    {
        var log = new EventLog();
        var reg = new FakeRegistryService(log);
        var restore = new RecordingRestorePointService(log);
        return (new RestoreManagerService(restore, reg), reg);
    }

    [Fact]
    public async Task SetUnthrottled_True_WritesZeroDword()
    {
        var (svc, reg) = New();

        var ok = await svc.SetUnthrottledAsync(unthrottle: true);

        Assert.True(ok);
        Assert.True(reg.TryReadValue("HKLM", SrKey, FreqValue, out var v));
        Assert.Equal("0", v);
    }

    /// <summary>Restoring the throttle DELETES the override so the absent key = Windows' default 24h limit (true inverse).</summary>
    [Fact]
    public async Task SetUnthrottled_False_DeletesValue_RestoringDefault()
    {
        var (svc, reg) = New();
        reg.Seed("HKLM", SrKey, FreqValue, "0");   // pretend the throttle was lifted earlier

        var ok = await svc.SetUnthrottledAsync(unthrottle: false);

        Assert.True(ok);
        Assert.False(reg.TryReadValue("HKLM", SrKey, FreqValue, out _));   // gone → absent = Windows default
    }
}
