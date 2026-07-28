// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Geocoding.Features.Geocoding.ReferenceDataImport;

namespace Honua.Core.Tests.Features.Geocoding;

/// <summary>
/// Unit coverage for the streaming CSV record reader used by the geocoder reference data loader.
/// </summary>
public sealed class GeocoderReferenceCsvTests
{
    [Fact]
    public async Task ReadRecordsAsync_PlainRecords_ParsesFieldsAndSkipsBlankLines()
    {
        var records = await ReadAllAsync("a,b,c\r\n1,2,3\n\n4,5,6\n");

        Assert.Equal(3, records.Count);
        Assert.Equal(["a", "b", "c"], records[0]);
        Assert.Equal(["1", "2", "3"], records[1]);
        Assert.Equal(["4", "5", "6"], records[2]);
    }

    [Fact]
    public async Task ReadRecordsAsync_QuotedFields_HandlesCommasNewlinesAndEscapedQuotes()
    {
        var records = await ReadAllAsync("name,note\n\"Redlands, CA\",\"line1\nline2\"\n\"say \"\"hi\"\"\",x\n");

        Assert.Equal(3, records.Count);
        Assert.Equal(["Redlands, CA", "line1\nline2"], records[1]);
        Assert.Equal(["say \"hi\"", "x"], records[2]);
    }

    [Fact]
    public async Task ReadRecordsAsync_NoTrailingNewline_ReturnsFinalRecord()
    {
        var records = await ReadAllAsync("a,b\n1,2");

        Assert.Equal(2, records.Count);
        Assert.Equal(["1", "2"], records[1]);
    }

    [Fact]
    public async Task ReadRecordsAsync_EmptyTrailingField_Preserved()
    {
        var records = await ReadAllAsync("a,b\n1,\n");

        Assert.Equal(["1", ""], records[1]);
    }

    [Fact]
    public async Task ReadRecordsAsync_UnterminatedQuote_Throws()
    {
        await Assert.ThrowsAsync<GeocoderReferenceDataImportException>(() => ReadAllAsync("a,b\n\"open,2\n"));
    }

    private static async Task<List<string[]>> ReadAllAsync(string csv)
    {
        using var reader = new StringReader(csv);
        var records = new List<string[]>();
        await foreach (var record in GeocoderReferenceCsv.ReadRecordsAsync(reader))
        {
            records.Add(record);
        }

        return records;
    }
}
