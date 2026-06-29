using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="ScheduledTaskStateParser"/> — the read of the CSV that
/// <c>Get-ScheduledTask | Select TaskPath,TaskName,State | ConvertTo-Csv</c> emits behind the « Tâches planifiées »
/// page. The load-bearing honesty points: the full task path is <c>TaskPath + TaskName</c> (exactly what
/// <c>schtasks /Change /TN</c> wants), a task counts as disabled ONLY when PowerShell says so (the invariant
/// "Disabled"/"1" — never the localized schtasks text), and a task we can't find is simply absent from the map,
/// never invented as enabled-or-disabled.
/// </summary>
public class ScheduledTaskStateParserTests
{
    // A representative ConvertTo-Csv block: the quoted header row + three tasks, one disabled.
    private const string SampleCsv = @"""TaskPath"",""TaskName"",""State""
""\Microsoft\Windows\Application Experience\"",""Microsoft Compatibility Appraiser"",""Disabled""
""\Microsoft\Windows\Maps\"",""MapsUpdateTask"",""Ready""
""\Microsoft\Windows\Feedback\Siuf\"",""DmClient"",""Running""";

    [Fact]
    public void Parse_JoinsTaskPathAndName_IntoTheFullSchtasksPath()
    {
        var map = ScheduledTaskStateParser.Parse(SampleCsv);

        Assert.True(map.ContainsKey(@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser"));
        Assert.True(map.ContainsKey(@"\Microsoft\Windows\Maps\MapsUpdateTask"));
        Assert.True(map.ContainsKey(@"\Microsoft\Windows\Feedback\Siuf\DmClient"));
    }

    [Fact]
    public void Parse_ReadsEnabledStateFromPowerShell()
    {
        var map = ScheduledTaskStateParser.Parse(SampleCsv);

        Assert.False(map[@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser"]); // Disabled
        Assert.True(map[@"\Microsoft\Windows\Maps\MapsUpdateTask"]);    // Ready
        Assert.True(map[@"\Microsoft\Windows\Feedback\Siuf\DmClient"]); // Running
    }

    [Fact]
    public void Parse_SkipsTheColumnHeaderRow()
        // The "TaskPath/TaskName/State" header must not become a phantom task.
        => Assert.False(ScheduledTaskStateParser.Parse(SampleCsv).ContainsKey("TaskPathTaskName"));

    [Theory]
    [InlineData("Disabled", false)]
    [InlineData("disabled", false)]   // case-insensitive
    [InlineData("DISABLED", false)]
    [InlineData("1", false)]          // the numeric form of the ScheduledTaskState enum
    [InlineData("Ready", true)]
    [InlineData("Running", true)]
    [InlineData("Queued", true)]
    [InlineData("3", true)]           // Ready as a number — anything not "Disabled"/"1" can still fire
    public void Parse_DisabledIsRecognisedTextAndNumeric_EverythingElseEnabled(string state, bool expectedEnabled)
    {
        var map = ScheduledTaskStateParser.Parse($"\"\\X\\\",\"Task\",\"{state}\"");
        Assert.Equal(expectedEnabled, map[@"\X\Task"]);
    }

    [Fact]
    public void Parse_HandlesCrlfLineEndings()
    {
        var crlf = "\"TaskPath\",\"TaskName\",\"State\"\r\n\"\\X\\\",\"Task\",\"Disabled\"";
        var map = ScheduledTaskStateParser.Parse(crlf);
        Assert.False(map[@"\X\Task"]);   // the trailing '\r' didn't corrupt the state read
    }

    [Fact]
    public void Parse_KeepsCommaInsideQuotedFieldWhole()
    {
        // A comma inside a quoted TaskName must NOT split the field — the RFC-4180 quoting is honoured.
        var map = ScheduledTaskStateParser.Parse("\"\\X\\\",\"Weird, name\",\"Ready\"");
        Assert.True(map.ContainsKey(@"\X\Weird, name"));
    }

    [Fact]
    public void Parse_UnescapesDoubledQuote()
    {
        // ConvertTo-Csv emits a literal quote as "" — it must decode back to a single quote.
        var map = ScheduledTaskStateParser.Parse("\"\\X\\\",\"Na\"\"me\",\"Ready\"");
        Assert.True(map.ContainsKey("\\X\\Na\"me"));
    }

    [Fact]
    public void Parse_IgnoresStrayTypeHeaderLine()
        => Assert.Empty(ScheduledTaskStateParser.Parse("#TYPE Selected.Microsoft.Management.Infrastructure.CimInstance"));

    [Fact]
    public void Parse_KeyLookupIsCaseInsensitive()
    {
        var map = ScheduledTaskStateParser.Parse("\"\\Microsoft\\Windows\\Maps\\\",\"MapsUpdateTask\",\"Ready\"");
        Assert.True(map.ContainsKey(@"\microsoft\windows\maps\mapsupdatetask"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Parse_EmptyOrNull_ReturnsEmpty(string? csv)
        => Assert.Empty(ScheduledTaskStateParser.Parse(csv));
}
