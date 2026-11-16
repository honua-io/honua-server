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
            BinaryExpression binary => CanonicalizePermanentFilterBinary(binary, resource),
            UnaryExpression unary => unary with
            {
                Operand = CanonicalizePermanentFilter(unary.Operand, resource)
            },
            ValueList values => new ValueList(values.Values
                .Select(value => CanonicalizePermanentFilter(value, resource)).ToArray()),
            Literal { Type: LiteralType.Number, Value: not (byte or sbyte or short or ushort or int or uint or long) } =>
                throw new InvalidOperationException("Saved permanent filter numeric literal cannot be evaluated consistently."),
            Literal { Type: LiteralType.Text, Value: not (string or null) } =>
                throw new InvalidOperationException("Saved permanent filter text literal cannot be evaluated consistently."),
            Literal literal when literal.Type is LiteralType.Text or LiteralType.Number or LiteralType.Boolean or LiteralType.Null => literal,
            _ => throw new InvalidOperationException("Saved permanent filter cannot be evaluated consistently.")
        };

    private static BinaryExpression CanonicalizePermanentFilterBinary(
        BinaryExpression binary,
        MetadataV2Object resource)
    {
        if (binary.Operator is not (BinaryOperator.And or BinaryOperator.Or or
            BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.In or BinaryOperator.NotIn))
        {
            throw new InvalidOperationException("Saved permanent filter operator cannot be evaluated consistently.");
        }

        var left = CanonicalizePermanentFilter(binary.Left, resource);
        var right = CanonicalizePermanentFilter(binary.Right, resource);
        if (binary.Operator is BinaryOperator.And or BinaryOperator.Or)
        {
            return binary with { Left = left, Right = right };
        }

        var leftKind = PermanentFilterOperandKind(left, resource);
        var operands = right is ValueList list ? list.Values : [right];
        foreach (var operand in operands)
        {
            var rightKind = PermanentFilterOperandKind(operand, resource);
            if (leftKind != LiteralType.Null && rightKind != LiteralType.Null && leftKind != rightKind)
            {
                throw new InvalidOperationException("Saved permanent filter operand types cannot be evaluated consistently.");
            }
        }

        return binary with { Left = left, Right = right };
    }

    private static LiteralType PermanentFilterOperandKind(FilterExpression expression, MetadataV2Object resource)
        => expression switch
        {
            Literal literal => literal.Type,
            PropertyReference property => resource.SchemaFields.Single(field => field.Name == property.PropertyName).Type switch
            {
                MetadataV2FieldType.String => LiteralType.Text,
                MetadataV2FieldType.Boolean => LiteralType.Boolean,
                _ => LiteralType.Number
            },
            _ => throw new InvalidOperationException("Saved permanent filter operand cannot be evaluated consistently.")
        };

    private static PropertyReference ResolvePermanentFilterProperty(
        PropertyReference property,
        MetadataV2Object resource)
    {
        var fields = resource.SchemaFields.Where(field =>
            string.Equals(field.Name, property.PropertyName, StringComparison.OrdinalIgnoreCase)).ToArray();
        // System fields map to physical SQL columns rather than event attributes.
        // Temporal, UUID and floating-point attributes need SQL type conversions the evaluator does not apply.
        if (property.PropertyName.ToUpperInvariant() is
            "ID" or "OBJECTID" or "OBJECT_ID" or "LAYERID" or "LAYER_ID" or "GEOM" or "GEOMETRY" or "SHAPE")
        {
            throw new InvalidOperationException("Saved permanent filter field cannot be evaluated consistently.");
        }
        if (fields.Length != 1 || fields[0].Type is not
            (MetadataV2FieldType.String or MetadataV2FieldType.Integer or MetadataV2FieldType.BigInteger or
             MetadataV2FieldType.Boolean))
        {
            throw new InvalidOperationException("Saved permanent filter field cannot be evaluated consistently.");
        }

        return new PropertyReference(fields[0].Name);
    }
}
