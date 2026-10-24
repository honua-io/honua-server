// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Db.DuckDB.Queries.Filters;

namespace Honua.Db.DuckDB.Tests;

public sealed class DuckDbSqlFilterTranslatorTests
{
    [Fact]
    public void Translate_IsNullUnaryForUnknownField_ThrowsTypedException()
    {
        var filter = new UnaryExpression(
            UnaryOperator.IsNull,
            new PropertyReference("collection_specific_field"));

        var exception = Assert.Throws<UnknownFilterFieldException>(
            () => new DuckDbSqlFilterTranslator().Translate(filter, CreateResource()));

        Assert.Equal("collection_specific_field", exception.PropertyName);
    }

    [Fact]
    public void Translate_SpatialUnknownField_ThrowsTypedException()
    {
        var filter = new SpatialPredicate(
            SpatialOperator.Intersects,
            new PropertyReference("collection_specific_field"),
            new PropertyReference("geometry"));

        var exception = Assert.Throws<UnknownFilterFieldException>(
            () => new DuckDbSqlFilterTranslator().Translate(filter, CreateResource()));

        Assert.Equal("collection_specific_field", exception.PropertyName);
    }

    private static MetadataV2Resource CreateResource()
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-parcels", Name = "parcels" },
            Type = MetadataV2ResourceType.FeatureDataset,
            SchemaFields =
            [
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String },
            ],
        };
}
