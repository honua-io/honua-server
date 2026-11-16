// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Contract;

public sealed class GeoServicesFieldTypeClientContractTests
{
    private static readonly string[] FixedIdentityFieldTypes = ["esriFieldTypeOID", "esriFieldTypeGlobalID"];

    [UnitTest]
    public void MapFieldType_EveryCanonicalFieldType_IsAcceptedByClientContract()
    {
        var contract = LoadClientContractFieldTypes();

        var advertised = Enum.GetValues<MetadataV2FieldType>()
            .Select(type => (Type: type, GeoServicesType: GeoServicesFieldConventions.MapFieldType(type)))
            .ToList();

        advertised.Where(pair => !contract.Contains(pair.GeoServicesType))
            .Select(pair => $"{pair.Type} -> {pair.GeoServicesType}")
            .Should()
            .BeEmpty("every field type GeoServices advertises must be accepted by the client contract in tests/js/shared/esri-field-types.json");
        contract.Should().Contain(FixedIdentityFieldTypes);
    }

    [UnitTest]
    public void MapFieldType_CalendarDateAndTimestamp_AdvertiseDistinctEsriTypes()
    {
        GeoServicesFieldConventions.MapFieldType(MetadataV2FieldType.Date).Should().Be("esriFieldTypeDateOnly");
        GeoServicesFieldConventions.MapFieldType(MetadataV2FieldType.DateTime).Should().Be("esriFieldTypeDate");
    }

    private static HashSet<string> LoadClientContractFieldTypes()
    {
        var path = RepositoryPaths.Resolve("tests", "js", "shared", "esri-field-types.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("fieldTypes")
            .EnumerateArray()
            .Select(element => element.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }
}
