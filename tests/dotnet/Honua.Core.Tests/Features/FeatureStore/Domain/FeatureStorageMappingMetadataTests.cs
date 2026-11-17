// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.FeatureStore.Domain;

public sealed class FeatureStorageMappingMetadataTests
{
    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("parcels", null, "parcels")]
    [InlineData(" public.parcels ", "public", "parcels")]
    [InlineData("database.public.parcels", "public", "parcels")]
    [InlineData(".public..parcels.", "public", "parcels")]
    [InlineData("\"my schema\".\"my\"\"table\"", "my schema", "my\"table")]
    public void FromMetadata_RelationalLocator_PreservesIdentifierParsing(
        string locator, string? schema, string table)
    {
        var mapping = FeatureStorageMapping.FromMetadata(new MetadataV2Object(),
            new MetadataV2StorageBinding { Locator = locator });

        mapping.SchemaName.Should().Be(schema);
        mapping.TableName.Should().Be(table);
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("...")]
    [InlineData("https://example.com/parcels")]
    [InlineData("database:parcels")]
    public void FromMetadata_InvalidLocator_RejectsEmptyTable(string locator)
    {
        var action = () => FeatureStorageMapping.FromMetadata(new MetadataV2Object(),
            new MetadataV2StorageBinding { Locator = locator });

        action.Should().Throw<InvalidOperationException>().WithMessage("*table or view*");
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("{\"schemaName\":\"preferred\",\"schema\":\"alias\",\"tableName\":\"chosen\",\"table\":\"alias\"}", "preferred.chosen")]
    [InlineData("{\"schema\":\"alias\",\"table\":\"alias_table\"}", "alias.alias_table")]
    [InlineData("{\"schemaName\":\"override\",\"tableName\":\" \"}", "override.parcels")]
    [InlineData("{\"schemaName\":\"\"}", "parcels")]
    public void FromMetadata_ExplicitOptions_PreservePrecedence(string options, string qualifiedName)
    {
        var mapping = FeatureStorageMapping.FromMetadata(new MetadataV2Object(), Binding(options));

        mapping.QualifiedName.Should().Be(qualifiedName);
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("4326", 4326)]
    [InlineData("\" +3857 \"", 3857)]
    [InlineData("2147483647", int.MaxValue)]
    [InlineData("2147483648", 4269)]
    [InlineData("\"2147483648\"", 4269)]
    [InlineData("4326.5", 4269)]
    [InlineData("true", 4269)]
    [InlineData("null", 4269)]
    public void FromMetadata_SridOption_PreservesIntegerConversionAndFallback(string value, int expected)
    {
        var binding = Binding("{\"storageSrid\":" + value + ",\"srid\":4269}");
        var mapping = FeatureStorageMapping.FromMetadata(new MetadataV2Object(), binding);

        mapping.StorageSrid.Should().Be(expected);
    }

    [UnitTest]
    public void FromMetadata_NonStringOptions_PreserveProviderValuesAndDiscriminator()
    {
        const string options = "{\"tableName\":123,\"sourceBacked\":true,\"custom\":null,\"layerDiscriminatorColumn\":\"layer_id\"}";
        var binding = Binding(options) with { StorageLayerId = 17 };
        var mapping = FeatureStorageMapping.FromMetadata(new MetadataV2Object(), binding);

        mapping.TableName.Should().Be("123");
        mapping.IsSourceBacked.Should().BeTrue();
        mapping.ProviderOptions["custom"].Should().BeEmpty();
        mapping.LayerDiscriminatorColumn.Should().Be("layer_id");
        mapping.LayerDiscriminatorValue.Should().Be(17);
    }

    private static MetadataV2StorageBinding Binding(string options)
    {
        using var document = JsonDocument.Parse(options);
        return new MetadataV2StorageBinding
        {
            Locator = "public.parcels",
            Options = document.RootElement.EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.Clone())
        };
    }
}
