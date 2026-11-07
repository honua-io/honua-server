// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Metadata;

[Protocol(ProtocolNames.TestQuality)]
[Operation(Operations.Metadata)]
public sealed class PublishedMetadataProjectionTests
{
    [UnitTheory]
    [InlineData("{}", true)]
    [InlineData("{\"advertised\":true}", true)]
    [InlineData("{\"advertised\":false}", false)]
    public void DeserializePublication_AdvertisingField_PreservesDefaultAndExplicitValues(string json, bool expected)
    {
        var publication = JsonSerializer.Deserialize(json, MetadataV2JsonContext.Default.MetadataV2Publication)!;
        Assert.Equal(expected, publication.Advertised);
        var roundTrip = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(publication, MetadataV2JsonContext.Default.MetadataV2Publication),
            MetadataV2JsonContext.Default.MetadataV2Publication)!;
        Assert.Equal(expected, roundTrip.Advertised);
    }
}
