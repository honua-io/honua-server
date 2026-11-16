// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Temporal.Abstractions;
using Honua.Core.Features.Temporal.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[Protocol(TestProtocols.TestQuality)]
[Collection("Database")]
public sealed class PostgresTemporalTimelineTests(WebAppFixture fixture) : IClassFixture<WebAppFixture>
{
    [IntegrationTheory]
    [InlineData(1)]
    [InlineData(10)]
    [Operation(Operations.ExtractChanges)]
    public async Task GetFeatureRevisionsAsync_CapturedBoundary_ExcludesLaterChanges(int limit)
    {
        var schema = (Honua.Infrastructure.Middleware.SchemaContext)fixture.GetService<ISchemaContext>();
        var previousSchema = schema.CurrentSchema;
        schema.CurrentSchema = fixture.CurrentSchema;
        try
        {
            var generations = await InsertRevisionsAsync();
            var store = fixture.GetService<ITemporalHistoryStore>();
            var query = new TemporalFeatureRevisionQuery
            {
                StorageLayerId = 990106,
                ObjectId = 190106,
                ThroughGeneration = generations[1],
                Limit = limit
            };

            var captured = await store.GetFeatureRevisionsAsync(query);
            captured.Select(row => row.Generation).Should().Equal(generations.Take(2).Take(limit));
            var later = await store.GetFeatureRevisionsAsync(query with
            {
                AfterGeneration = generations[1],
                ThroughGeneration = generations[2]
            });
            later.Should().ContainSingle().Which.Generation.Should().Be(generations[2]);
        }
        finally
        {
            schema.CurrentSchema = previousSchema;
        }
    }

    private async Task<long[]> InsertRevisionsAsync()
    {
        await using var connection = await fixture.Postgres.GetConnectionAsync(fixture.CurrentSchema!);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM honua.feature_changes WHERE layer_id = 990106;
            INSERT INTO honua.feature_changes (generation, layer_id, objectid, operation)
            SELECT nextval('honua.sync_generation'), 990106, 190106, 2
            FROM generate_series(1, 3)
            RETURNING generation;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        var generations = new List<long>();
        while (await reader.ReadAsync())
        {
            generations.Add(reader.GetInt64(0));
        }
        return generations.ToArray();
    }
}
