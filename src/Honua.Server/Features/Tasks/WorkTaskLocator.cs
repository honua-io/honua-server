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
internal sealed class WorkTaskLocator(
    IAssetProfileStore profiles,
    IFormTargetMetadataResolver layerResolver,
    IFeatureReader featureReader,
    IFilterExpressionService filters,
    ILayerAccessAuthorizer layerAccess)
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

        var profile = await profiles.GetAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (profile is null || !string.Equals(profile.ServiceId, request.ServiceId, StringComparison.Ordinal))
        {
            return WorkTaskLocation.Fail("profileId", "The asset profile was not found on this service.");
        }

        if (profile.State != AssetProfileState.Active)
        {
            return WorkTaskLocation.Fail("profileId", "Tasks can only target assets of an active profile.");
        }

        var target = await layerResolver.ResolveAsync(
            new FormTargetDefinition { ServiceId = profile.ServiceId, LayerId = profile.LayerId },
            cancellationToken).ConfigureAwait(false);
        if (target is not { Publication: { } publication, Resource: { } resource, StorageLayerId: int })
        {
            return WorkTaskLocation.Fail("profileId", "The profiled layer is no longer published.");
        }

        var access = await layerAccess.AuthorizePublicationAsync(user, publication, resource, target.Service, AuthorizationOperation.Query, cancellationToken)
            .ConfigureAwait(false);
        return access.IsAllowed
            ? await CopyAssetGeometryAsync(target, profile, request.AssetId, cancellationToken).ConfigureAwait(false)
            : WorkTaskLocation.Fail("assetId", "No asset with this ID exists on the layer.");
    }

    private async Task<WorkTaskLocation> CopyAssetGeometryAsync(
        FormTargetMetadataResolution target,
        AssetProfile profile,
        string assetId,
        CancellationToken cancellationToken)
    {
        var resource = target.Resource!;
        if (AssetIdLiteral(resource, profile.Fields.AssetId!, assetId) is not { } literal)
        {
            return WorkTaskLocation.Fail("assetId", "No asset with this ID exists on the layer.");
        }

        var match = new BinaryExpression(new PropertyReference(profile.Fields.AssetId!), BinaryOperator.Equal, literal);
        var translation = filters.Translate(match, resource);
        if (!translation.IsSuccess || translation.SqlFilter is null)
        {
            return WorkTaskLocation.Fail("assetId", "The asset ID cannot be matched on this layer.");
        }

        // Two rows are read so a non-unique asset ID is refused rather than resolved arbitrarily.
        var result = await featureReader.QueryAsync(
            target.StorageLayerId!.Value,
            new FeatureQuery { SqlFilter = translation.SqlFilter, Limit = 2 },
            cancellationToken).ConfigureAwait(false);
        var features = result.Items.Take(2).ToList();
        return features switch
        {
            [] => WorkTaskLocation.Fail("assetId", "No asset with this ID exists on the layer."),
            [{ Geometry: { Length: > 0 } wkb }] => WorkTaskLocation.FromAsset(wkb, resource.ReadSrid() ?? Wgs84, CanonicalId(literal)),
            [_] => WorkTaskLocation.Fail("assetId", "The asset has no geometry."),
            _ => WorkTaskLocation.Fail("assetId", "The asset ID is not unique on the layer; fix the data or the profile's ID field."),
        };
    }

    // Tasks and summaries key on this spelling, so "007" and "7" name one numeric asset.
    private static string CanonicalId(Literal literal) => Convert.ToString(literal.Value, CultureInfo.InvariantCulture)!;

    // Numeric and UUID identity fields compare in their canonical form; an ID that does not parse cannot match one.
    private static Literal? AssetIdLiteral(MetadataV2Resource resource, string fieldName, string assetId)
    {
        var field = resource.SchemaFields.FirstOrDefault(f => string.Equals(f.Name, fieldName, StringComparison.Ordinal));
        return field?.Type switch
        {
            MetadataV2FieldType.Integer or MetadataV2FieldType.BigInteger
                => long.TryParse(assetId, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? new Literal(number, LiteralType.Number) : null,
            MetadataV2FieldType.Uuid => Guid.TryParse(assetId, out var uuid) ? new Literal(uuid.ToString("D"), LiteralType.Text) : null,
            _ => new Literal(assetId, LiteralType.Text),
        };
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
