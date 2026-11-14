// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests;

[Collection("Database.CoreEndpoints")]
[Protocol(ProtocolNames.FeatureServer)]
public sealed class BinaryQueryWriteEndpointTests
{
    [IntegrationTheory]
    [InlineData("pbf")]
    [InlineData("parquet")]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_LargeBinaryPage_WritesBoundedChunksOnRepeatedRequests(string format)
    {
        var writes = new ResponseWriteProbe();
        var fixture = new WebAppFixture().ConfigureServices(services =>
            services.AddSingleton<IStartupFilter>(writes));
        await fixture.InitializeAsync();
        try
        {
            await fixture.EnsureLargeTestDatasetAsync();
            await using (var connection = await fixture.Postgres.GetConnectionAsync(fixture.CurrentSchema!))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE features SET attributes = attributes || jsonb_build_object('Name',
                        (SELECT string_agg(md5(objectid::text || ':' || n::text), '') FROM generate_series(1, 128) n))
                    WHERE layer_id = 0;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var path = $"/rest/services/test/FeatureServer/0/query?where=1%3D1&f={format}&outFields=Name&resultRecordCount=300";
            for (var attempt = 0; attempt < 2; attempt++)
            {
                writes.LargestWrite = 0;
                using var response = await fixture.Client.GetAsync(path);
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync();
                bytes.Length.Should().BeGreaterThan(64 * 1024);
                response.Content.Headers.ContentType!.MediaType.Should().Be(format == "pbf"
                    ? "application/x-protobuf" : "application/vnd.apache.parquet");
                writes.LargestWrite.Should().BeLessThanOrEqualTo(64 * 1024);
                if (format == "parquet")
                {
                    using var stream = new MemoryStream(bytes);
                    using var reader = new ParquetSharp.Arrow.FileReader(stream);
                    reader.NumRowGroups.Should().BeGreaterThan(0);
                }
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private sealed class ResponseWriteProbe : IStartupFilter
    {
        public int LargestWrite { get; set; }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                var original = context.Response.Body;
                await using var captured = new WriteProbeStream(this);
                context.Response.Body = captured;
                try
                {
                    await continuation();
                    captured.Position = 0;
                    await captured.CopyToAsync(original);
                }
                finally
                {
                    context.Response.Body = original;
                }
            });
            next(app);
        };

        private sealed class WriteProbeStream(ResponseWriteProbe probe) : MemoryStream
        {
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                probe.LargestWrite = Math.Max(probe.LargestWrite, buffer.Length);
                return base.WriteAsync(buffer, cancellationToken);
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                probe.LargestWrite = Math.Max(probe.LargestWrite, count);
                return base.WriteAsync(buffer, offset, count, cancellationToken);
            }
        }
    }
}
