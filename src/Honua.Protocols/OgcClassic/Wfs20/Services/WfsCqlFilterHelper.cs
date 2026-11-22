// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Microsoft.AspNetCore.Http;
using WfsQueryException = Honua.Protocols.Ogc.Classic.Wfs20.Services.Wfs20Handler.WfsQueryException;

namespace Honua.Protocols.Ogc.Classic.Wfs20.Services;

internal static class WfsCqlFilterHelper
{
    private const string HttpContextItemKey = "Honua.Wfs.CqlFilter";
    private static readonly AsyncLocal<string?> CurrentCqlFilter = new();

    internal static void SetRequestCqlFilter(HttpContext context, string? cqlFilter)
    {
        if (!string.IsNullOrWhiteSpace(cqlFilter))
        {
            context.Items[HttpContextItemKey] = cqlFilter;
        }
        else
        {
            context.Items.Remove(HttpContextItemKey);
        }
    }

    internal static string? GetRequestCqlFilter(HttpContext context)
    {
        if (context.Items.TryGetValue(HttpContextItemKey, out var value) && value is string stored)
        {
            return stored;
        }

        foreach (var pair in context.Request.Query)
        {
            if (string.Equals(pair.Key, Wfs20Utilities.ParameterNames.CqlFilter, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value.FirstOrDefault();
            }
        }

        return null;
    }

    internal static string? Current => CurrentCqlFilter.Value;

    internal static IDisposable BeginScope(string? cqlFilter)
    {
        var previous = CurrentCqlFilter.Value;
        CurrentCqlFilter.Value = cqlFilter;
        return new ScopeReset(previous);
    }

    internal static string?[]? ResolvePerTypeFilters(string? cqlFilter, string? fesFilter, int typeCount)
    {
        if (string.IsNullOrWhiteSpace(cqlFilter))
        {
            return null;
        }

        EnsureNoFesConflict(fesFilter);
        var tokens = OgcClassicCqlFilter.SplitTokens(cqlFilter);
        if (tokens.Length != 1 && tokens.Length != typeCount)
        {
            throw new WfsQueryException(
                "InvalidParameterValue",
                $"CQL_FILTER must contain exactly 1 or {typeCount.ToString(CultureInfo.InvariantCulture)} filter(s), separated by semicolons, matching the number of TYPENAMES.",
                "cql_filter");
        }

        var perType = new string?[typeCount];
        for (var i = 0; i < typeCount; i++)
        {
            perType[i] = tokens.Length == 1 ? tokens[0] : tokens[i];
        }

        return perType;
    }

    internal static string? ResolveSingleTypeFilter(string? cqlFilter, string? fesFilter)
    {
        if (string.IsNullOrWhiteSpace(cqlFilter))
        {
            return null;
        }

        EnsureNoFesConflict(fesFilter);
        var tokens = OgcClassicCqlFilter.SplitTokens(cqlFilter);
        if (tokens.Length != 1)
        {
            throw new WfsQueryException(
                "InvalidParameterValue",
                "CQL_FILTER must contain exactly 1 filter matching the requested feature type.",
                "cql_filter");
        }

        return tokens[0];
    }

    internal static void EnsureSingleExpressionForXmlQueries(string? cqlFilter)
    {
        if (!string.IsNullOrWhiteSpace(cqlFilter) && OgcClassicCqlFilter.SplitTokens(cqlFilter).Length != 1)
        {
            throw new WfsQueryException(
                "InvalidParameterValue",
                "CQL_FILTER on an XML GetFeature request must be a single expression; it applies to every Query.",
                "cql_filter");
        }
    }

    internal static SqlFragment? Translate(
        IFilterExpressionService filterExpressionService,
        MetadataV2Object resource,
        (string? CqlFilter, string? FesFilter) filters)
    {
        if (string.IsNullOrWhiteSpace(filters.CqlFilter))
        {
            return null;
        }

        EnsureNoFesConflict(filters.FesFilter);
        var (filter, error) = OgcClassicCqlFilter.Translate(filterExpressionService, resource, filters.CqlFilter);
        return error is null ? filter : throw new WfsQueryException("InvalidParameterValue", error, "cql_filter");
    }

    private static void EnsureNoFesConflict(string? fesFilter)
    {
        if (!string.IsNullOrWhiteSpace(fesFilter))
        {
            throw new WfsQueryException(
                "InvalidParameterValue",
                "FILTER and CQL_FILTER cannot be combined in the same request.",
                "cql_filter");
        }
    }

    private sealed class ScopeReset(string? previous) : IDisposable
    {
        public void Dispose() => CurrentCqlFilter.Value = previous;
    }
}
