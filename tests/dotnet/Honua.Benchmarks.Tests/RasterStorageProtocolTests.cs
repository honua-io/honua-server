// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Honua.Benchmarks.RasterStorage;
using Xunit;

namespace Honua.Benchmarks.Tests;

public sealed class RasterStorageProtocolTests
{
    [Fact]
    public void Create_StorageWorkloadMatrix_IsExhaustive()
    {
        var protocol = RasterStorageProtocol.Create();

        Assert.Equal(
            Enum.GetValues<RasterStorageLayout>().Length * Enum.GetValues<RasterStorageWorkload>().Length,
            protocol.Cells.Count);
        Assert.All(
            protocol.Cells.GroupBy(cell => (cell.Layout, cell.Workload)),
            group => Assert.Single(group));
    }

    [Fact]
    public void Create_CogAndZarrCells_DoNotClaimUnsupportedParity()
    {
        var protocol = RasterStorageProtocol.Create();
        var cog = protocol.Cells.Where(cell => cell.Layout == RasterStorageLayout.ObjectCog).ToArray();
        var zarr = protocol.Cells.Where(cell => cell.Layout == RasterStorageLayout.ObjectZarr).ToArray();
        var hybrid = protocol.Cells.Where(cell => cell.Layout == RasterStorageLayout.HybridCogPostgis).ToArray();

        Assert.Equal(BenchmarkSupport.Runnable, Assert.Single(cog, cell => cell.Workload == RasterStorageWorkload.Tile).Support);
        Assert.All(cog.Where(cell => cell.Workload != RasterStorageWorkload.Tile), cell =>
        {
            Assert.Equal(BenchmarkSupport.Unsupported, cell.Support);
            Assert.False(string.IsNullOrWhiteSpace(cell.Reason));
        });
        Assert.All(zarr, cell => Assert.Equal(BenchmarkSupport.Unsupported, cell.Support));
        Assert.All(hybrid, cell => Assert.Equal(BenchmarkSupport.Unsupported, cell.Support));
    }

    [Fact]
    public void Create_MixedGridFixture_FailsCanonicalAlignment()
    {
        var protocol = RasterStorageProtocol.Create();
        var aligned = protocol.Fixtures.Single(fixture => fixture.Id == "aligned-mosaic");
        var mixed = protocol.Fixtures.Single(fixture => fixture.Id == "mixed-grid-mosaic");

        Assert.True(RasterGridAlignment.Analyze(aligned.Scenes).IsAligned);
        var result = RasterGridAlignment.Analyze(mixed.Scenes);
        Assert.False(result.IsAligned);
        Assert.Contains(result.Issues, issue => issue.Contains("pixel lattice", StringComparison.Ordinal));
        Assert.Contains(result.Issues, issue => issue.Contains("scale", StringComparison.Ordinal));
    }

    [Fact]
    public void Percentile_UsesDeterministicNearestRank()
    {
        double[] samples = [9, 1, 5, 7, 3, 11, 13, 15, 17, 19];

        Assert.Equal(9, RasterStorageStatistics.Percentile(samples, 0.50));
        Assert.Equal(19, RasterStorageStatistics.Percentile(samples, 0.95));
    }

    [Fact]
    public void ValidateRun_UnsupportedCogCellCannotBeReportedCompleted()
    {
        var protocol = RasterStorageProtocol.Create();
        var result = new RasterStorageWorkloadResult(
            RasterStorageLayout.ObjectCog,
            "small-raster",
            RasterStorageWorkload.Statistics,
            BenchmarkResultStatus.Completed,
            0,
            [1],
            1,
            1,
            [],
            null);
        var run = CreateRun([result]);

        var exception = Assert.Throws<InvalidDataException>(() => RasterStorageProtocolValidator.ValidateRun(protocol, run));
        Assert.Contains("explicitly unsupported", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Protocol_SerializesAndDeserializes_WithSourceGeneratedContract()
    {
        var protocol = RasterStorageProtocol.Create();

        var json = JsonSerializer.Serialize(protocol, RasterStorageJsonContext.Default.RasterStorageProtocolDefinition);
        var roundTrip = JsonSerializer.Deserialize(json, RasterStorageJsonContext.Default.RasterStorageProtocolDefinition);

        Assert.NotNull(roundTrip);
        RasterStorageProtocolValidator.ValidateDefinition(roundTrip);
    }

    [Fact]
    public async Task CountingHttpRangeReader_RecordsOnlyMeasuredRangeTraffic()
    {
        var handler = new RangeResponseHandler();
        using var client = new HttpClient(handler);
        var reader = new CountingHttpRangeReader(client, new Uri("https://objects.example.test/fixture.tif"));

        var bytes = await reader.ReadRangeAsync(string.Empty, string.Empty, 4, 3, CancellationToken.None);

        Assert.Equal("456", Encoding.ASCII.GetString(bytes));
        Assert.Equal(1, reader.RequestCount);
        Assert.Equal(3, reader.BytesRead);
        Assert.Equal(10, await reader.GetObjectSizeAsync(string.Empty, string.Empty, CancellationToken.None));
        Assert.Equal(1, reader.RequestCount);
    }

    private static RasterStorageBenchmarkRun CreateRun(IReadOnlyList<RasterStorageWorkloadResult> results)
        => new(
            RasterStorageProtocol.Version,
            "test-run",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddSeconds(1),
            new RasterStorageEnvironment("test", "test", 1, null, null, null, null, "test"),
            results);

    private sealed class RangeResponseHandler : HttpMessageHandler
    {
        private static readonly byte[] Content = "0123456789"u8.ToArray();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var range = Assert.IsType<RangeItemHeaderValue>(Assert.Single(request.Headers.Range!.Ranges));
            var start = Assert.IsType<long>(range.From);
            var end = Assert.IsType<long>(range.To);
            var length = checked((int)(end - start + 1));
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(Content.AsSpan((int)start, length).ToArray()),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, Content.LongLength);
            return Task.FromResult(response);
        }
    }
}
