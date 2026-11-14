// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Honua.Core.Features.Assets;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Tasks;

// Resolves where a new task is: a geometry snapshot copied from the asset feature, or the caller's explicit location.
// Asset reads go through the layer access check and the feature reader, which applies row-level security itself.
internal sealed class WorkTaskLocator(WorkTaskAssetResolver assets)
{
    private const int Wgs84 = 4326;
    private const int MaxAssetIdLength = 200;
    private const int MaxLocationVertices = 10_000;
    private static readonly Envelope Wgs84Bounds = new(-180, 180, -90, 90);

    public async Task<WorkTaskLocation> LocateAsync(WorkTaskCreateRequest request, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (request.ProfileId is null && request.AssetId is null)
        {
            return ParseExplicit(request.Location);
        }

        if (request.ProfileId is not Guid profileId || string.IsNullOrWhiteSpace(request.AssetId) || request.AssetId.Length > MaxAssetIdLength)
        {
            return WorkTaskLocation.Fail("assetId", "profileId and assetId must be given together.");
        }

        var asset = await assets.ResolveAsync(new WorkTaskAssetReference(profileId, request.ServiceId!, request.AssetId), user, cancellationToken)
            .ConfigureAwait(false);
        if (asset.Error is { } error)
        {
            return new WorkTaskLocation(null, 0, null, error);
        }

        return asset.Feature is { Geometry: { Length: > 0 } wkb }
            ? WorkTaskLocation.FromAsset(wkb, asset.Target!.Value.Resource!.ReadSrid() ?? Wgs84, asset.CanonicalId!)
            : WorkTaskLocation.Fail("assetId", "The asset has no geometry.");
    }

    private static WorkTaskLocation ParseExplicit(JsonElement? location)
    {
        if (location is not { ValueKind: JsonValueKind.Object } element)
        {
            return WorkTaskLocation.Fail("location", "A task without an asset needs a GeoJSON location.");
        }

        Geometry? geometry;
        try
        {
            geometry = new GeoJsonReader().Read<Geometry>(element.GetRawText());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            geometry = null;
        }

        if (geometry is null || geometry.IsEmpty || geometry.NumPoints > MaxLocationVertices ||
            !Wgs84Bounds.Covers(geometry.EnvelopeInternal) || !geometry.IsValid)
        {
            return WorkTaskLocation.Fail("location", $"location must be a valid, non-empty WGS 84 GeoJSON geometry of at most {MaxLocationVertices} vertices.");
        }

        return new WorkTaskLocation(new WKBWriter().Write(geometry), Wgs84, "explicit", null);
    }
}

internal sealed record WorkTaskLocation(byte[]? Wkb, int Srid, string? Source, AssetFieldError? Error)
{
    public string? AssetId { get; init; }

    public static WorkTaskLocation FromAsset(byte[] wkb, int srid, string assetId) => new(wkb, srid, "asset", null) { AssetId = assetId };

    public static WorkTaskLocation Fail(string field, string message) => new(null, 0, null, new AssetFieldError(field, message));
}
