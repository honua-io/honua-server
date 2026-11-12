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

internal sealed class WorkTaskAssetGeometry(
    IFeatureReader featureReader,
    IFilterExpressionService filters,
    ILayerAccessAuthorizer layerAccess)
{
    private const int Wgs84 = 4326;

    public async Task<WorkTaskLocation> LocateAsync(
        FormTargetMetadataResolution target,
        (AssetProfile Profile, string AssetId, ClaimsPrincipal User) asset,
        CancellationToken cancellationToken)
    {
        var access = await layerAccess.AuthorizePublicationAsync(
            asset.User, target.Publication!, target.Resource!, target.Service, AuthorizationOperation.Query, cancellationToken)
            .ConfigureAwait(false);
        return access.IsAllowed
            ? await CopyAsync(target, (asset.Profile, asset.AssetId), cancellationToken).ConfigureAwait(false)
            : WorkTaskLocation.Fail("assetId", "No asset with this ID exists on the layer.");
    }

    public async Task<WorkTaskLocation> CopyAsync(
        FormTargetMetadataResolution target,
        (AssetProfile Profile, string AssetId) asset,
        CancellationToken cancellationToken)
    {
        var (profile, assetId) = asset;
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
            new FeatureQuery { SqlFilter = translation.SqlFilter, Limit = 2, OutputSrid = resource.ReadSrid() ?? Wgs84 },
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
                => long.TryParse(assetId, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ? new Literal(number, LiteralType.Number) : null,
            MetadataV2FieldType.Uuid => Guid.TryParse(assetId, out var uuid) ? new Literal(uuid.ToString("D"), LiteralType.Text) : null,
            _ => new Literal(assetId, LiteralType.Text),
        };
    }

}
