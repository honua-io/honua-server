// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

public sealed partial class PostgresStorageMappedFeatureReaderEncodedFormatsIntegrationTests
{
    private static readonly string[] UnicodeColumnNames = ["名前", "länge_straße", "ʻāina_inoa", "\U00020BB7_name", "AND\u0301x"];

    [Fact]
    public async Task QueryPageAsync_NonAsciiPhysicalColumns_AreReturnedAndFilterable()
    {
        await _fixture.ExecuteAsync($"""
            ALTER TABLE {_schema}.cities ADD COLUMN "名前" text, ADD COLUMN "länge_straße" text,
                ADD COLUMN "ʻāina_inoa" text, ADD COLUMN "𠮷_name" text, ADD COLUMN "AND́x" text;
            UPDATE {_schema}.cities SET "名前" = '名-A1', "länge_straße" = 'L-A1', "ʻāina_inoa" = 'ʻA-A1', "𠮷_name" = 'S-A1', "AND́x" = 'M-A1' WHERE objectid = 1;
            UPDATE {_schema}.cities SET "名前" = '名-B2', "länge_straße" = 'L-B2', "ʻāina_inoa" = 'ʻA-B2' WHERE objectid = 2;
            """);
        var reader = CreateReader(additionalFields: UnicodeTextFields());

        var all = (await reader.QueryPageAsync(1, new FeatureQuery { Limit = 10 })).Items.ToList();
        all.Should().HaveCount(2);
        all[0].Attributes.Should().Contain("名前", "名-A1")
            .And.Contain("länge_straße", "L-A1")
            .And.Contain("ʻāina_inoa", "ʻA-A1");

        var filtered = await reader.QueryPageAsync(1, new FeatureQuery { Limit = 10, Where = "名前 = '名-B2'" });
        filtered.Items.Select(feature => feature.Id).Should().Equal(2);
        var supplementary = await reader.QueryPageAsync(1, new FeatureQuery { Limit = 10, Where = "\U00020BB7_name = 'S-A1'" });
        supplementary.Items.Select(feature => feature.Id).Should().Equal(1);
        var combining = await reader.QueryPageAsync(1, new FeatureQuery { Limit = 10, Where = "AND\u0301x = 'M-A1'" });
        combining.Items.Select(feature => feature.Id).Should().Equal(1);

        var projected = await reader.QueryPageAsync(1, new FeatureQuery
        {
            Limit = 10,
            Where = "länge_straße IS NOT NULL",
            OutFields = ["länge_straße"]
        });
        projected.Items.Should().HaveCount(2);
        projected.Items.First().Attributes.Keys.Should().Contain("länge_straße").And.NotContain("名前");
    }

    [Fact]
    public async Task QueryPageAsync_NonAsciiJsonbAttributeKeys_AreReturned()
    {
        await _fixture.ExecuteAsync($"""
            UPDATE {_schema}.cities SET attributes = jsonb_build_object('名前', '名-A1', 'länge_straße', 'L-A1', 'ʻāina_inoa', 'ʻA-A1')
                WHERE objectid = 1;
            """);
        var reader = CreateReader(attributesColumn: "attributes", additionalFields: UnicodeTextFields());

        var page = await reader.QueryPageAsync(1, new FeatureQuery { Limit = 10 });

        page.Items.Single(feature => feature.Id == 1).Attributes.Should()
            .Contain("名前", "名-A1")
            .And.Contain("länge_straße", "L-A1")
            .And.Contain("ʻāina_inoa", "ʻA-A1");
    }

    private static MetadataV2Field[] UnicodeTextFields()
        => UnicodeColumnNames
            .Select(name => new MetadataV2Field { Name = name, Type = MetadataV2FieldType.String })
            .ToArray();
}
