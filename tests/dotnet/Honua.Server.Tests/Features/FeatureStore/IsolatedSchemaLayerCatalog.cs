// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Server.Tests.Infrastructure;

namespace Honua.Server.Tests.Features.FeatureStore;

/// <summary>
/// The feature store reads layer SRIDs from the layers catalog in its own configured schema, as
/// production does. A test that builds the store over an isolated schema therefore needs catalog
/// rows in that schema; rows only in the shared honua.layers table leave geometries at SRID 0.
/// </summary>
internal static class IsolatedSchemaLayerCatalog
{
    public static Task SeedIsolatedLayerCatalogAsync(
        this DatabaseFixtureAdapter fixture,
        string schema,
        params (int LayerId, string Name, string GeometryType)[] layers)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var values = string.Join(
            ",\n",
            layers.Select(layer => string.Create(
                CultureInfo.InvariantCulture,
                $"({layer.LayerId}, '{layer.Name}', '{schema}', 'features', '{layer.GeometryType}', 4326)")));

        return fixture.ExecuteAsync($"""
            CREATE TABLE IF NOT EXISTS {schema}.layers (LIKE honua.layers INCLUDING ALL);
            INSERT INTO {schema}.layers (layer_id, layer_name, table_schema, table_name, geometry_type, srid)
            VALUES {values}
            ON CONFLICT (layer_id) DO NOTHING;
            """);
    }
}
