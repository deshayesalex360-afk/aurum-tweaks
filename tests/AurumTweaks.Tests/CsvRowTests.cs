using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="CsvRow.Split"/> — the shared RFC-4180 line splitter behind both PowerShell-CSV state parsers
/// (scheduled tasks, Appx). The load-bearing points: a comma inside a quoted field never splits it, a doubled
/// <c>""</c> decodes to one literal quote, and empty fields are preserved positionally (so column N stays column N).
/// </summary>
public class CsvRowTests
{
    [Fact]
    public void Split_PlainQuotedFields_StripsQuotes()
        => Assert.Equal(new[] { "a", "b", "c" }, CsvRow.Split("\"a\",\"b\",\"c\""));

    [Fact]
    public void Split_KeepsCommaInsideQuotedFieldWhole()
        => Assert.Equal(new[] { "a", "b, still b", "c" }, CsvRow.Split("\"a\",\"b, still b\",\"c\""));

    [Fact]
    public void Split_UnescapesDoubledQuote()
        => Assert.Equal(new[] { "Na\"me" }, CsvRow.Split("\"Na\"\"me\""));

    [Fact]
    public void Split_PreservesEmptyFieldsPositionally()
        => Assert.Equal(new[] { "a", "", "c" }, CsvRow.Split("a,,c"));

    [Fact]
    public void Split_UnquotedFields_AreTakenVerbatim()
        => Assert.Equal(new[] { "Microsoft.BingNews", "True" }, CsvRow.Split("Microsoft.BingNews,True"));

    [Fact]
    public void Split_SingleField_ReturnsOneElement()
        => Assert.Equal(new[] { "solo" }, CsvRow.Split("solo"));
}
