// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Claims;
using Honua.Core.Features.Assets;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;

namespace Honua.Server.Features.Tasks;

internal sealed class WorkTaskAssetReader(
    FeatureProviderQueryRouter providers,
    IFilterExpressionService filters,
    ILayerAccessAuthorizer layerAccess)
{
    public async Task<WorkTaskAsset> ResolveAsync(
        (FormTargetMetadataResolution Target, AssetProfile Profile, string AssetId, AuthorizationOperation Operation) subject,
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var target = subject.Target;
        var access = await layerAccess.AuthorizePublicationAsync(user, target.Publication!, target.Resource!, target.Service,
            subject.Operation, cancellationToken).ConfigureAwait(false);
        return access.IsAllowed
            ? await ReadAsync((target, subject.Profile, subject.AssetId), cancellationToken).ConfigureAwait(false)
            : WorkTaskAsset.Fail("assetId", "No asset with this ID exists on the layer.");
    }

    public async Task<bool> AuthorizeUpdateAsync(WorkTaskAsset asset, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var target = asset.Target!.Value;
        var access = await layerAccess.AuthorizePublicationAsync(user, target.Publication!, target.Resource!, target.Service,
            AuthorizationOperation.Update, cancellationToken).ConfigureAwait(false);
        return access.IsAllowed;
    }

    private async Task<WorkTaskAsset> ReadAsync(
        (FormTargetMetadataResolution Target, AssetProfile Profile, string AssetId) subject,
        CancellationToken cancellationToken)
    {
        var (target, profile, assetId) = subject;
        var resource = target.Resource!;
        if (AssetIdLiteral(resource, profile.Fields.AssetId!, assetId) is not { } literal)
        {
            return WorkTaskAsset.Fail("assetId", "No asset with this ID exists on the layer.");
        }

        var match = new BinaryExpression(new PropertyReference(profile.Fields.AssetId!), BinaryOperator.Equal, literal);
        var translation = filters.Translate(match, resource);
        if (!translation.IsSuccess || translation.SqlFilter is null)
        {
            return WorkTaskAsset.Fail("assetId", "The asset ID cannot be matched on this layer.");
        }

        try
        {
            var reader = await providers.ResolveReaderAsync(target.Snapshot!, target.Service!, resource,
                target.Publication!, target.StorageLayerId!.Value, FeatureProviderReadOperation.Query, cancellationToken).ConfigureAwait(false);
            var result = await reader.QueryAsync(target.StorageLayerId.Value,
                new FeatureQuery { SqlFilter = translation.SqlFilter, Limit = 2 }, cancellationToken).ConfigureAwait(false);
            var matched = MatchAsset((target, profile, literal), result.Items.Take(2).ToList());
            return matched.Feature is { } feature
                ? await ReadSnapshotAsync(reader, matched, feature.Id, cancellationToken).ConfigureAwait(false)
                : matched;
        }
        catch (InvalidOperationException)
        {
            return WorkTaskAsset.Fail("assetId", "The asset ID cannot be matched on this layer.");
        }
    }

    private static async Task<WorkTaskAsset> ReadSnapshotAsync(
        IFeatureReader reader, WorkTaskAsset asset, long featureId, CancellationToken cancellationToken)
    {
        var target = asset.Target!.Value;
        // Get captures the complete managed row token before applying the visible schema projection.
        var feature = await reader.GetAsync(target.StorageLayerId!.Value, featureId, cancellationToken).ConfigureAwait(false);
        var idField = asset.Profile!.Fields.AssetId!;
        if (feature is not { } snapshot || !snapshot.Attributes.TryGetValue(idField, out var value) ||
            AssetIdLiteral(target.Resource!, idField, Convert.ToString(value, CultureInfo.InvariantCulture)!) is not { } literal ||
            CanonicalId(literal) != asset.CanonicalId)
        {
            return WorkTaskAsset.Fail("assetId", "No asset with this ID exists on the layer.");
        }

        return asset with { Feature = snapshot };
    }

    private static WorkTaskAsset MatchAsset(
        (FormTargetMetadataResolution Target, AssetProfile Profile, Literal Id) subject, List<Feature> features)
    {
        var (target, profile, literal) = subject;
        return features switch
        {
            [] => WorkTaskAsset.Fail("assetId", "No asset with this ID exists on the layer."),
            [var feature] => new WorkTaskAsset { Profile = profile, Target = target, Feature = feature, CanonicalId = CanonicalId(literal) },
            _ => WorkTaskAsset.Fail("assetId", "The asset ID is not unique on the layer; fix the data or the profile's ID field."),
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

}
