// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Helpers;
using Honua.Protocols.Ogc.Classic.Wfs20.Services;

namespace Honua.Protocols.Ogc.Classic;

// The vendor CQL_FILTER parameter shared by WMS GetMap/GetFeatureInfo and WFS GetFeature.
// Each protocol maps the returned error text into its own exception document.
internal static class OgcClassicCqlFilter
{
    internal static string[] SplitTokens(string cqlFilterParam)
    {
        var tokens = new List<string>();
        var start = 0;
        var activeQuote = '\0';

        // A doubled quote inside a literal toggles twice, so it leaves the literal open.
        for (var i = 0; i < cqlFilterParam.Length; i++)
        {
            var c = cqlFilterParam[i];
            if (activeQuote == '\0' && c is '\'' or '"')
            {
                activeQuote = c;
            }
            else if (c == activeQuote)
            {
                activeQuote = '\0';
            }
            else if (activeQuote == '\0' && c == ';')
            {
                tokens.Add(cqlFilterParam[start..i].Trim());
                start = i + 1;
            }
        }

        tokens.Add(cqlFilterParam[start..].Trim());
        return tokens.ToArray();
    }

    internal static (SqlFragment? Filter, string? Error) Translate(
        IFilterExpressionService service,
        MetadataV2Object resource,
        string cqlText)
    {
        var publicResource = resource with
        {
            SchemaFields = resource.SchemaFields.Where(field => !field.Hidden).ToArray()
        };

        var (expression, error) = ParseAndNormalize(service, publicResource, cqlText);
        if (expression is null)
        {
            return (null, error);
        }

        var translation = service.Translate(expression, publicResource);
        return translation.IsSuccess
            ? (translation.SqlFilter, null)
            : (null, translation.ErrorMessage ?? "Invalid CQL_FILTER expression.");
    }

    private static (FilterExpression? Expression, string? Error) ParseAndNormalize(
        IFilterExpressionService service,
        MetadataV2Object publicResource,
        string cqlText)
    {
        var parseResult = service.Parse(FilterLanguage.Cql2Text, cqlText);
        if (!parseResult.IsSuccess || parseResult.Expression is null)
        {
            return (null, $"Invalid CQL_FILTER expression: {parseResult.ErrorMessage ?? "syntax error."}");
        }

        FilterExpression expression;
        try
        {
            expression = FilterExpressionHelpers.NormalizeFilterPropertyReferences(
                parseResult.Expression,
                publicResource,
                propertyName => WfsPropertyNameResolver.Resolve(publicResource, propertyName, allowGeometryAlias: true));
            expression = service.Normalize(expression, publicResource);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return (null, ex.Message);
        }

        return FilterExpressionHelpers.IsBooleanFilterExpression(expression)
            ? (expression, null)
            : (null, "CQL_FILTER expression must be a boolean predicate.");
    }
}
