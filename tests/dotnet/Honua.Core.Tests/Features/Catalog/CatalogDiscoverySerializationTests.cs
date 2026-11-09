// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using Honua.Core.Features.Catalog;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Catalog;

public sealed class CatalogDiscoverySerializationTests
{
    [UnitTest]
    public void Deserialize_UnknownNestedFields_PreservesCatalogAndOptions()
    {
        const string json = """
            {"metadata":{"id":"discovery"},"target":"discovery","status":{},"options":{"unfamiliar":true},
             "configuration":{"futureSetting":{"mode":"next"},"audience":{"kind":"public","futureAudience":7},
             "scope":{"kinds":["layer"],"futureScope":false},"defaultListing":"automatic"}}
            """;
        var catalog = JsonSerializer.Deserialize(json, MetadataV2JsonContext.Default.MetadataV2Catalog)!;
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(catalog, MetadataV2JsonContext.Default.MetadataV2Catalog));
        Assert.True(serialized.RootElement.GetProperty("options").GetProperty("unfamiliar").GetBoolean());
        var configuration = serialized.RootElement.GetProperty("configuration");
        Assert.Equal("next", configuration.GetProperty("futureSetting").GetProperty("mode").GetString());
        Assert.Equal(7, configuration.GetProperty("audience").GetProperty("futureAudience").GetInt32());
        Assert.False(configuration.GetProperty("scope").GetProperty("futureScope").GetBoolean());
        Assert.Equal("layer", Assert.Single(catalog.Configuration!.Scope.Kinds));
    }

    [UnitTest]
    public void Deserialize_ExplicitNullAudience_DoesNotBecomePublic()
    {
        var catalog = JsonSerializer.Deserialize("""
            {"configuration":{"audience":null,"defaultListing":"automatic","scope":{"kinds":["layer"]}}}
            """, MetadataV2JsonContext.Default.MetadataV2Catalog)!;
        Assert.False(CatalogMembershipPolicy.AllowsAudience(catalog.Configuration!.Audience, new(new ClaimsIdentity())));
    }
    [UnitTheory]
    [InlineData("{}", true)]
    [InlineData("{\"advertised\":true}", true)]
    [InlineData("{\"advertised\":false}", false)]
    public void Deserialize_LayerAdvertising_PreservesOmittedDefaultAndExplicitValues(string json, bool expected)
    {
        var layer = JsonSerializer.Deserialize(json, MetadataV2JsonContext.Default.MetadataV2Publication)!;
        Assert.Equal(expected, layer.Advertised);
    }
}
