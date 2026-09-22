// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Contract;

/// <summary>
/// Keeps the Esri field types the GeoServices projections advertise inside the client
/// field-type contract that the JavaScript integration suite enforces. That suite only
/// runs in the trailing matrix, so a new advertised type (for example
/// <c>esriFieldTypeDateOnly</c> for calendar dates, #4833) must fail here, on the PR,
/// until <c>tests/js/shared/esri-field-types.json</c> accepts it.
/// </summary>
public sealed class GeoServicesFieldTypeClientContractTests
{
    private static readonly string[] FixedIdentityFieldTypes = ["esriFieldTypeOID", "esriFieldTypeGlobalID"];

    [UnitTest]
    public void MapFieldType_EveryCanonicalFieldType_IsAcceptedByClientContract()
    {
        var contract = LoadClientContractFieldTypes();

        var advertised = Enum.GetValues<MetadataV2FieldType>()
            .Select(type => (Type: type, EsriType: GeoServicesFieldConventions.MapFieldType(type)))
            .ToList();

        advertised.Where(pair => !contract.Contains(pair.EsriType))
            .Select(pair => $"{pair.Type} -> {pair.EsriType}")
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
