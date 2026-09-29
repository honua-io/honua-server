// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.TestKit;
using Npgsql;
using NpgsqlTypes;
using Xunit.Abstractions;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

[Collection("Database")]
public sealed class SourceAttributeJsonDocumentAllocationTests(PostgresFixture fixture, ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadJsonb_DirectDocument_AllocatesLessThanStringRoundTrip(bool nested)
    {
        const int rows = 1000;
        const string scalarJson = """{"id":9223372036854775807,"feature_name":"Kāneʻohe 🌋","category":"park","temperature":23.5,"population":1000,"active":true,"missing":null,"status":"open","region":"west","date":"2026-09-26T00:00:00Z","description":"quote\" and newline\n"}""";
        const string nestedJson = """{"id":9223372036854775807,"feature_name":"Kāneʻohe 🌋","category":"park","temperature":23.5,"population":1000,"active":true,"missing":null,"status":"open","region":"west","date":"2026-09-26T00:00:00Z","details":{"coordinates":[1,2],"value":null},"items":[1,"two",{"ok":true}]}""";
        var payload = nested ? nestedJson : scalarJson;
        var destination = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();

        // Keep this loop synchronous so thread-local allocation accounting does
        // not span an await. Both variants include command/reader, JSON decode,
        // canonical scalar conversion, nested cloning and the immutable copy.
        long Measure(bool directDocument)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            using var command = new NpgsqlCommand(
                directDocument
                    ? "SELECT $1::jsonb FROM generate_series(1, 1000)"
                    : "SELECT ($1::jsonb)::text FROM generate_series(1, 1000)", connection);
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = payload });
            using var reader = command.ExecuteReader();
            var count = 0;
            while (reader.Read())
            {
                if (directDocument)
                {
                    using var document = reader.GetFieldValue<JsonDocument>(0);
                    FeatureAttributeJsonReader.ReadInto(document.RootElement, destination);
                }
                else
                {
                    FeatureAttributeJsonReader.ReadInto(reader.GetString(0), destination);
                }

                GC.KeepAlive(destination.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
                destination.Clear();
                count++;
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            count.Should().Be(rows);
            return allocated / rows;
        }

        _ = Measure(false);
        _ = Measure(true);
        var textSamples = new List<long>();
        var documentSamples = new List<long>();
        for (var repetition = 0; repetition < 5; repetition++)
        {
            if (repetition % 2 == 0)
            {
                textSamples.Add(Measure(false));
                documentSamples.Add(Measure(true));
            }
            else
            {
                documentSamples.Add(Measure(true));
                textSamples.Add(Measure(false));
            }
        }

        var textMedian = textSamples.Order().ElementAt(2);
        var documentMedian = documentSamples.Order().ElementAt(2);
        output.WriteLine($"JSONB decode allocated bytes/row (nested={nested}): text=[{string.Join(",", textSamples)}], document=[{string.Join(",", documentSamples)}]");
        documentMedian.Should().BeLessThan(textMedian * 95 / 100,
            "removing the UTF-16 string should reduce per-row allocations before changing the production reader");
    }
}
