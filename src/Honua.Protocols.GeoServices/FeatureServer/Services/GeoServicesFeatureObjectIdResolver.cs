// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Resolves a protocol-facing GeoServices object id to the stored feature row.
/// </summary>
internal static class GeoServicesFeatureObjectIdResolver
{
    public static async Task<Feature?> ResolveAsync(
        IFeatureReader featureReader,
        IFilterExpressionService filterExpressionService,
        MetadataV2Resource resource,
        int storageLayerId,
        long objectId,
        CancellationToken cancellationToken)
    {
        var objectIdField = GeoServicesObjectIdFieldResolver.ResolveObjectIdField(resource);
        if (objectIdField is null || objectIdField.Name.Equals(FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase))
        {
            return await featureReader.GetAsync(storageLayerId, objectId, cancellationToken).ConfigureAwait(false);
        }

        var expression = new BinaryExpression(
            new PropertyReference(objectIdField.Name),
            BinaryOperator.In,
            new ValueList([new Literal(objectId, LiteralType.Number)]));
        var translation = filterExpressionService.Translate(expression, resource);
        if (!translation.IsSuccess)
        {
            throw new ArgumentException(translation.ErrorMessage ?? "Invalid ObjectId field.");
        }

        var result = await featureReader.QueryAsync(
            storageLayerId,
            new FeatureQuery
            {
                SqlFilter = translation.SqlFilter,
                Limit = 2,
            },
            cancellationToken).ConfigureAwait(false);

        return result.Items.SingleOrDefault(feature =>
            feature.Attributes.TryGetValue(objectIdField.Name, out var value)
            && FeatureServerValueParser.TryConvertToLong(value, out var resolvedObjectId)
            && resolvedObjectId == objectId);
    }
}
