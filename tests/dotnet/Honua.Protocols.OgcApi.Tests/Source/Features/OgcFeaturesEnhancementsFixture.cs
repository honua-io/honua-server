// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

public sealed class OgcFeaturesEnhancementsFixture : IAsyncLifetime
{
    public WebAppFixture Application { get; } = CreateApplication();

    public Task InitializeAsync() => InitializeApplicationAsync(Application);

    public Task DisposeAsync() => Application.DisposeAsync();

    internal static WebAppFixture CreateApplication() =>
        new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    internal static async Task InitializeApplicationAsync(WebAppFixture application)
    {
        await application.InitializeAsync();
        ConfigureQueryMetadata(application);
    }

    private static void ConfigureQueryMetadata(WebAppFixture application)
    {
        // Match the seeded WGS84 point layer and its timestamp/event_date interval.
        application.UpdateV2ResourceMetadata(
            WebAppFixture.TestLayerId,
            spatial: new Honua.Core.Features.Metadata.Domain.V2.MetadataV2ObjectSpatial
            {
                GeometryType = Honua.Core.Features.Metadata.Domain.V2.MetadataV2GeometryType.Point,
                SpatialReference = new Honua.Core.Features.Metadata.Domain.V2.MetadataV2SpatialReference
                {
                    Srid = 4326,
                    Crs = "EPSG:4326",
                    IsGeographic = true,
                },
            },
            temporal: new Honua.Core.Features.Metadata.Domain.V2.MetadataV2ObjectTemporal
            {
                StartTimeField = "timestamp",
                EndTimeField = "event_date",
            });
    }
}
