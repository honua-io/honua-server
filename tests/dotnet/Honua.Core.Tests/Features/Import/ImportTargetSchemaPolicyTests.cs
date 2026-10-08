// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Microsoft.Extensions.Configuration;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// SEC-23: import targets are limited to the configured operational schemas; the server metadata
/// schema and PostgreSQL system schemas are refused even when listed as operational.
/// </summary>
public sealed class ImportTargetSchemaPolicyTests
{
    private static readonly string[] OperationalSchemas = ["honua_data", "public", "ops"];

    [Theory]
    [InlineData("honua")]
    [InlineData("HONUA")]
    [InlineData(" honua ")]
    [InlineData("pg_catalog")]
    [InlineData("pg_toast")]
    [InlineData("PG_temp_3")]
    [InlineData("information_schema")]
    public void IsReserved_MetadataOrSystemSchema_ReturnsTrue(string schema)
    {
        ImportTargetSchemaPolicy.IsReserved(schema, metadataSchemas: null).Should().BeTrue();
    }

    [Theory]
    [InlineData("honua_data")]
    [InlineData("public")]
    [InlineData("ops")]
    public void IsReserved_OperationalSchema_ReturnsFalse(string schema)
    {
        ImportTargetSchemaPolicy.IsReserved(schema, metadataSchemas: null).Should().BeFalse();
    }

    [Theory]
    [InlineData("honua_data")]
    [InlineData("public")]
    [InlineData("ops")]
    [InlineData(" OPS ")]
    public void IsAllowed_ConfiguredOperationalSchema_ReturnsTrue(string schema)
    {
        ImportTargetSchemaPolicy.IsAllowed(schema, OperationalSchemas, metadataSchemas: null).Should().BeTrue();
    }

    [Theory]
    [InlineData("staging")]
    [InlineData("_scratch")]
    [InlineData("honua")]
    [InlineData("pg_catalog")]
    [InlineData("information_schema")]
    public void IsAllowed_SchemaOutsideOperationalList_ReturnsFalse(string schema)
    {
        ImportTargetSchemaPolicy.IsAllowed(schema, OperationalSchemas, metadataSchemas: null).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsAllowed_OmittedSchema_ReturnsTrue(string? schema)
    {
        // An omitted schema resolves to the configured operational default downstream.
        ImportTargetSchemaPolicy.IsAllowed(schema, OperationalSchemas, metadataSchemas: null).Should().BeTrue();
    }

    [Fact]
    public void IsAllowed_ReservedSchemaListedAsOperational_ReturnsFalse()
    {
        ImportTargetSchemaPolicy.IsAllowed("honua", ["honua_data", "honua"], metadataSchemas: null).Should().BeFalse();
        ImportTargetSchemaPolicy.IsAllowed("catalog_meta", ["honua_data", "catalog_meta"], ["catalog_meta"]).Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_ReadsSchemasFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Schema"] = "catalog_meta",
                ["Database:DefaultOperationalSchema"] = "gis",
                ["Database:OperationalSchemas:0"] = "ops",
                ["Database:OperationalSchemas:1"] = "catalog_meta"
            })
            .Build();

        ImportTargetSchemaPolicy.IsAllowed("gis", configuration).Should().BeTrue();
        ImportTargetSchemaPolicy.IsAllowed("public", configuration).Should().BeTrue();
        ImportTargetSchemaPolicy.IsAllowed("ops", configuration).Should().BeTrue();
        ImportTargetSchemaPolicy.IsAllowed("honua_data", configuration).Should().BeFalse();
        ImportTargetSchemaPolicy.IsAllowed("catalog_meta", configuration).Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_WithoutConfiguration_UsesProviderDefaults()
    {
        ImportTargetSchemaPolicy.IsAllowed("honua_data", configuration: null).Should().BeTrue();
        ImportTargetSchemaPolicy.IsAllowed("public", configuration: null).Should().BeTrue();
        ImportTargetSchemaPolicy.IsAllowed("ops", configuration: null).Should().BeFalse();
        ImportTargetSchemaPolicy.IsAllowed("honua", configuration: null).Should().BeFalse();
    }
}
