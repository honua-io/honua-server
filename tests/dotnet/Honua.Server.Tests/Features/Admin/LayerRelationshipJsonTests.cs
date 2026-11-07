// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Admin;

[Protocol(TestProtocols.Admin)]
[Operation(Operations.Metadata)]
public sealed class LayerRelationshipJsonTests
{
    [UnitTest]
    public void RelationshipUpdateJson_LegacyNumericId_RetainsSerializedName()
    {
        var item = JsonSerializer.Deserialize(
            """{"id":"relationship.test","name":"Test","relatedLayerId":4,"role":"origin","cardinality":"one-to-many","originField":"origin_id","destinationField":"destination_id","esriRelationshipId":3}""",
            LayerAuthoringJsonContext.Default.LayerRelationshipUpdateItem)!;

        Assert.Equal(3, item.GeoServicesRelationshipId);
        var json = JsonSerializer.Serialize(item, LayerAuthoringJsonContext.Default.LayerRelationshipUpdateItem);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(3, document.RootElement.GetProperty("esriRelationshipId").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("geoServicesRelationshipId", out _));
    }

    [UnitTest]
    public void RelationshipReadbackJson_LegacyNumericId_RetainsSerializedName()
    {
        var item = JsonSerializer.Deserialize(
            """{"id":"relationship.test","name":"Test","relatedLayerId":4,"role":"origin","cardinality":"one-to-many","originField":"origin_id","destinationField":"destination_id","esriRelationshipId":3}""",
            LayerAuthoringJsonContext.Default.LayerRelationshipItem)!;

        Assert.Equal(3, item.GeoServicesRelationshipId);
        var json = JsonSerializer.Serialize(item, LayerAuthoringJsonContext.Default.LayerRelationshipItem);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(3, document.RootElement.GetProperty("esriRelationshipId").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("geoServicesRelationshipId", out _));
    }
}
