// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;

namespace Honua.Server.Features.Streaming;

internal static partial class FeatureStreamEndpoints
{
    private static bool PermanentFilterAccepted(
        MetadataV2Object resource,
        IFilterExpressionService filterExpressionService,
        List<FilterExpression> predicates)
    {
        try
        {
            AppendPermanentFilterPredicate(resource, filterExpressionService, predicates);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal static void AppendPermanentFilterPredicate(
        MetadataV2Object resource,
        IFilterExpressionService filterExpressionService,
        List<FilterExpression> predicates)
    {
        ArgumentNullException.ThrowIfNull(predicates);
        var expression = Honua.Core.Features.FeatureStore.Services.LayerReadSecurityResolver
            .RequirePermanentFilterExpression(resource, filterExpressionService);
        if (expression is null)
        {
            return;
        }

        // The same depth guard as a subscriber filter. Past the limit the
        // evaluator fails closed, and a surrounding NOT would invert that.
        if (InMemoryFilterEvaluator.ExceedsMaxDepth(expression))
        {
            throw new InvalidOperationException(
                $"Saved permanent filter for resource '{resource.Metadata.Id}' exceeds the streaming depth limit.");
        }

        if (!InMemoryFilterEvaluator.TryValidateStreamingExpression(expression, out _))
        {
            throw new InvalidOperationException(
                $"Saved permanent filter for resource '{resource.Metadata.Id}' cannot be evaluated.");
        }

        predicates.Add(CanonicalizePermanentFilter(expression, resource));
    }

    private static FilterExpression CanonicalizePermanentFilter(
        FilterExpression expression,
        MetadataV2Object resource)
        => expression switch
        {
            PropertyReference property => ResolvePermanentFilterProperty(property, resource),
            BinaryExpression binary => binary with
            {
                Left = CanonicalizePermanentFilter(binary.Left, resource),
                Right = CanonicalizePermanentFilter(binary.Right, resource)
            },
            UnaryExpression unary => unary with
            {
                Operand = CanonicalizePermanentFilter(unary.Operand, resource)
            },
            ValueList values => new ValueList(values.Values
                .Select(value => CanonicalizePermanentFilter(value, resource)).ToArray()),
            Literal literal => literal,
            _ => throw new InvalidOperationException("Saved permanent filter cannot be evaluated consistently.")
        };

    private static PropertyReference ResolvePermanentFilterProperty(
        PropertyReference property,
        MetadataV2Object resource)
    {
        var fields = resource.SchemaFields.Where(field =>
            string.Equals(field.Name, property.PropertyName, StringComparison.OrdinalIgnoreCase)).ToArray();
        // Temporal attributes may be numeric epochs; the evaluator does not apply SQL's temporal casts.
        if (fields.Length != 1 || fields[0].Type is not
            (MetadataV2FieldType.String or MetadataV2FieldType.Integer or MetadataV2FieldType.BigInteger or
             MetadataV2FieldType.Double or MetadataV2FieldType.Float or MetadataV2FieldType.Boolean or MetadataV2FieldType.Uuid))
        {
            throw new InvalidOperationException("Saved permanent filter field cannot be evaluated consistently.");
        }

        return new PropertyReference(fields[0].Name);
    }
}
