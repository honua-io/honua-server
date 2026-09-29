// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

public sealed partial class PostgresStorageMappedFeatureReaderEncodedFormatsIntegrationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ReadFeatures_JsonbAttributes_PreservesTypesProjectionAndLifetime(bool useAttributes, bool streaming)
    {
        await _fixture.ExecuteAsync($"""
            ALTER TABLE {_schema}.cities ADD COLUMN rating double precision, ADD COLUMN active boolean,
                ADD COLUMN details jsonb, ADD COLUMN observed text, ADD COLUMN huge bigint;
            UPDATE {_schema}.cities SET name = 'Kāneʻohe 🌋', rating = 12.5, active = true,
                huge = 9223372036854775807, observed = '2026-09-26T00:00:00Z',
                details = jsonb_build_object('items', jsonb_build_array(1, NULL, jsonb_build_object('ok', true)),
                    'wide', repeat('🌋', 20000)) WHERE objectid = 1;
            UPDATE {_schema}.cities SET name = NULL, population = NULL, active = false WHERE objectid = 2;
            UPDATE {_schema}.cities SET attributes = jsonb_build_object('objectid', -1,
                'name', name, 'population', population, 'rating', rating, 'active', active,
                'huge', huge, 'observed', observed, 'details', details);
            """);
        var provider = CreateReader(attributesColumn: useAttributes ? "attributes" : null, additionalFields:
        [
            new MetadataV2Field { Name = "rating", Type = MetadataV2FieldType.Double },
            new MetadataV2Field { Name = "active", Type = MetadataV2FieldType.Boolean },
            new MetadataV2Field { Name = "huge", Type = MetadataV2FieldType.BigInteger },
            new MetadataV2Field { Name = "observed", Type = MetadataV2FieldType.String },
            new MetadataV2Field { Name = "details", Type = MetadataV2FieldType.Json }
        ]);

        async Task<List<Feature>> Read(FeatureQuery query)
        {
            if (!streaming)
            {
                return (await provider.QueryPageAsync(1, query)).Items.ToList();
            }

            var features = new List<Feature>();
            await foreach (var feature in provider.StreamFeaturesAsync(1, query))
            {
                features.Add(feature);
            }

            return features;
        }

        var query = new FeatureQuery { Limit = 10 };
        var result = await Read(query);
        result.Select(feature => feature.Id).Should().Equal(1, 2);
        var first = result[0].Attributes;
        first["objectid"].Should().BeOfType<long>().Which.Should().Be(1);
        first["name"].Should().Be("Kāneʻohe 🌋");
        first["population"].Should().BeOfType<long>().Which.Should().Be(350000);
        first["huge"].Should().BeOfType<long>().Which.Should().Be(long.MaxValue);
        first["rating"].Should().BeOfType<double>().Which.Should().Be(12.5);
        first["active"].Should().Be(true);
        first["observed"].Should().Be("2026-09-26T00:00:00Z");
        result[1].Attributes["name"].Should().BeNull();
        result[1].Attributes["population"].Should().BeNull();
        result[1].Attributes["details"].Should().BeNull();
        result[1].Attributes["active"].Should().Be(false);
        result.Should().OnlyContain(feature => feature.Geometry != null);

        var projected = await Read(query with
        {
            OutFields = ["name", "population", "details"],
            EnforcedMaskedFields = ["POPULATION"]
        });
        foreach (var feature in projected)
        {
            feature.Attributes.Keys.Should().BeEquivalentTo(["objectid", "name", "details"]);
        }

        var excluded = await Read(query with { ExcludeAttributes = true });
        foreach (var feature in excluded)
        {
            feature.Attributes.Keys.Should().Equal("objectid");
        }

        // The source documents, database readers and pooled dictionaries have
        // all been disposed/reused; nested values must remain independently owned.
        var details = first["details"].Should().BeOfType<JsonElement>().Subject;
        details.GetProperty("items")[2].GetProperty("ok").GetBoolean().Should().BeTrue();
        details.GetProperty("wide").GetString().Should().Be(string.Concat(Enumerable.Repeat("🌋", 20000)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadFeatures_DistinctNumericAttributes_PreservesTextOrdering(bool streaming)
    {
        await _fixture.ExecuteAsync($"""
            UPDATE {_schema}.cities SET population = CASE objectid WHEN 1 THEN 2 ELSE 10 END;
            """);
        var provider = CreateReader();
        var query = new FeatureQuery { Distinct = true, OutFields = ["population"], Limit = 10 };
        var features = new List<Feature>();
        if (streaming)
        {
            await foreach (var feature in provider.StreamFeaturesAsync(1, query))
            {
                features.Add(feature);
            }
        }
        else
        {
            features.AddRange((await provider.QueryPageAsync(1, query)).Items);
        }

        features.Select(feature => feature.Attributes["population"]).Should().Equal([10L, 2L],
            "the existing text ordering differs from JSONB numeric ordering");
    }

    [Theory]
    [InlineData(false, "jsonb")]
    [InlineData(true, "text")]
    public async Task BuildFeatureSelect_AttributeWireType_PreservesDistinctTextSemantics(bool distinct, string expectedType)
    {
        var query = new FeatureQuery { OutFields = ["name"], Limit = 100, Distinct = distinct };
        var sql = typeof(PostgresStorageMappedFeatureReader)
            .GetMethod("BuildFeatureSelect", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(CreateReader(), [query, false])!;
        var parameters = (IReadOnlyList<object?>)sql.GetType().GetProperty("Parameters")!.GetValue(sql)!;
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql.ToString();
        foreach (var value in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        }

        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetDataTypeName(2).Should().Be(expectedType,
            "ordinary reads should avoid a UTF-16 round trip while distinct comparison/order semantics remain text-based");
    }
}
