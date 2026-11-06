// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

public sealed partial class PostgresStorageMappedFeatureReaderVersionIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetAsync_ManagedSnapshotTokenUsesStoredAttributesWithoutExposingThem(bool secured)
    {
        await fixture.ExecuteAsync($"UPDATE {_schema}.features SET attributes = attributes || '{{\"storedOnly\":\"internal-value\"}}'::jsonb WHERE layer_id = 1 AND objectid = 1;");
        var feature = await CreateReader(secured: secured).GetAsync(1, 1);

        feature.Should().NotBeNull();
        var visible = feature!.Value;
        var stored = Feature.Create(1, visible.Geometry, new Dictionary<string, object?>
        {
            ["objectid"] = 1L, ["count"] = 1L, ["tenant"] = "a",
            ["secret"] = "base-one", ["storedOnly"] = "internal-value"
        }.ToImmutableDictionary());
        visible.ReadStateToken.Should().Be(FeatureStateToken.Compute(stored));
        visible.ReadStateToken.Should().NotBe(FeatureStateToken.Compute(visible));
        visible.Attributes.Should().NotContainKey("storedOnly");
        var json = JsonSerializer.Serialize(visible);
        json.Should().NotContain("internal-value").And.NotContain("ReadStateToken");
        if (secured)
        {
            visible.Attributes.Should().NotContainKey("secret");
            json.Should().NotContain("base-one");
        }
    }
}
