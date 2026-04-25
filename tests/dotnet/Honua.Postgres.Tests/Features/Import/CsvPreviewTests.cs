// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Services;
using Honua.Postgres.Features.Import;
using Honua.TestKit.Infrastructure;

namespace Honua.Postgres.Tests.Features.Import;

public sealed class CsvPreviewTests
{
    [Fact]
    public async Task PreviewFileAsync_CsvWithLongitudeLatitudeColumns_ReturnsPreviewMetadata()
    {
        var csv = """
            id,name,longitude,latitude,category
            1,San Francisco,-122.4194,37.7749,city
            2,Oakland,-122.2711,37.8044,city
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "sample.csv");

        preview.Format.Should().Be(SupportedFileFormat.Csv);
        preview.DetectedSrid.Should().Be(4326);
        preview.TotalFeatureCount.Should().Be(2);
        preview.SampleProperties.Should().ContainKey("name");
        preview.SampleProperties["name"].Should().Be("San Francisco");
    }

    [Fact]
    public async Task PreviewFileAsync_CsvWithWktGeometryColumn_ReturnsPreviewMetadata()
    {
        var csv = """
            id,name,wkt
            1,Test Feature,POINT(-122.4194 37.7749)
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "sample.csv");

        preview.Format.Should().Be(SupportedFileFormat.Csv);
        preview.TotalFeatureCount.Should().Be(1);
        preview.SampleProperties.Should().ContainKey("name");
        preview.SampleProperties["name"].Should().Be("Test Feature");
    }

    [Theory]
    [InlineData(';')]
    [InlineData('\t')]
    [InlineData('|')]
    public async Task PreviewFileAsync_CsvWithDetectedDelimiter_ReturnsPreviewMetadata(char delimiter)
    {
        var separator = delimiter == '\t' ? "\t" : delimiter.ToString();
        var csv = string.Join(
            Environment.NewLine,
            $"id{separator}name{separator}longitude{separator}latitude{separator}category",
            $"1{separator}San Francisco{separator}-122.4194{separator}37.7749{separator}city",
            $"2{separator}\"Half Moon Bay{separator} CA\"{separator}-122.4286{separator}37.4636{separator}town");

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "sample.csv");

        preview.Format.Should().Be(SupportedFileFormat.Csv);
        preview.DetectedSrid.Should().Be(4326);
        preview.TotalFeatureCount.Should().Be(2);
        preview.SampleProperties.Should().ContainKey("name");
        preview.SampleProperties["name"].Should().Be("San Francisco");
    }

    [Fact]
    public async Task ReadStreamingAsync_CsvWithExcessivelyLargeRecord_ThrowsInvalidOperationException()
    {
        // Create a CSV with an unbalanced quote that would cause unbounded memory allocation
        // This simulates a malicious CSV designed to exhaust memory
        var csvBuilder = new StringBuilder();
        csvBuilder.AppendLine("id,name,description");
        csvBuilder.Append("1,Test,\"This is a very long field that never closes the quote");

        // Add enough data to exceed the 10MB limit
        var largeData = new string('x', 5 * 1024 * 1024); // 5MB of data
        csvBuilder.AppendLine(largeData);
        csvBuilder.AppendLine(largeData); // Another 5MB, total > 10MB
        csvBuilder.AppendLine("more data to push over the limit");

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csvBuilder.ToString()));

        // The streaming reader should throw before consuming all available memory
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var feature in CsvFormatReader.ReadStreamingAsync(stream, CancellationToken.None))
            {
                // Should not reach here due to the memory limit
            }
        });

        exception.Message.Should().Contain("CSV record exceeds maximum size limit");
        exception.Message.Should().Contain("10,485,760 bytes");
    }

    private static IFileImportService CreateService() =>
        PreviewImportServiceFactory.Create();

}
