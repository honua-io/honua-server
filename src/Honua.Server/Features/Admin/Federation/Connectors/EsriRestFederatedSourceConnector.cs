// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.Federation.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Admin.Federation.Connectors;

/// <summary>
/// HTTP <see cref="HttpGeoJsonFederatedSourceConnector"/> for Esri ArcGIS REST feature
/// services. It fetches GeoJSON from <c>{endpoint}/{remoteLayer}/query</c> with
/// <c>f=geojson</c>, pushing down the predicates the planner allows for the
/// <see cref="FederatedSourceKind.EsriRest"/> transport: a SQL-like <c>where</c> clause, an
/// <c>esriGeometryEnvelope</c> spatial filter, result ordering, and result paging. Exact
/// spatial relationships and temporal-interval filters are refined locally by the federation
/// executor, so this connector pushes only an envelope superset for them (issue #341).
/// </summary>
internal sealed class EsriRestFederatedSourceConnector : HttpGeoJsonFederatedSourceConnector
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EsriRestFederatedSourceConnector"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Factory for the named federation HTTP client.</param>
    /// <param name="logger">Logger for transport diagnostics.</param>
    public EsriRestFederatedSourceConnector(
        IHttpClientFactory httpClientFactory,
        ILogger<EsriRestFederatedSourceConnector> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override FederatedSourceKind Kind => FederatedSourceKind.EsriRest;

    /// <inheritdoc />
    protected override Uri BuildRequestUri(FederatedFetchRequest request)
    {
        var source = request.Source;
        var query = request.Query;

        var basePath = source.Endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var queryUrl = $"{basePath}/{Uri.EscapeDataString(source.RemoteLayer)}/query";

        var parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["f"] = "geojson",
            ["outFields"] = "*",
            ["returnGeometry"] = "true",
            // Esri requires a where clause; "1=1" selects all rows when no attribute filter
            // is pushed down.
            ["where"] = request.ShouldPushDown(FederationPredicateKind.AttributeFilter) &&
                        !string.IsNullOrWhiteSpace(query.Where)
                ? query.Where
                : "1=1",
        };

        if (TryGetPushedDownEnvelope(request, out var envelope))
        {
            parameters["geometryType"] = "esriGeometryEnvelope";
            parameters["spatialRel"] = "esriSpatialRelIntersects";
            parameters["geometry"] = string.Join(
                ',',
                Invariant(envelope.MinX),
                Invariant(envelope.MinY),
                Invariant(envelope.MaxX),
                Invariant(envelope.MaxY));

            if (envelope.Srid is { } srid)
            {
                parameters["inSR"] = srid.ToString(CultureInfo.InvariantCulture);
            }
        }

        if (request.ShouldPushDown(FederationPredicateKind.OrderBy) &&
            query.OrderBy is { IsDefaultOrEmpty: false } orderBy)
        {
            parameters["orderByFields"] = FormatOrderByFields(orderBy);
        }

        if (request.ShouldPushDown(FederationPredicateKind.Paging))
        {
            if (query.Limit is { } limit && limit > 0)
            {
                parameters["resultRecordCount"] = limit.ToString(CultureInfo.InvariantCulture);
            }

            if (query.Offset is { } offset && offset > 0)
            {
                parameters["resultOffset"] = offset.ToString(CultureInfo.InvariantCulture);
            }
        }

        return new Uri(QueryHelpers.AddQueryString(queryUrl, parameters));
    }

    private static string FormatOrderByFields(System.Collections.Immutable.ImmutableArray<OrderByClause> orderBy)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < orderBy.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            var clause = orderBy[i];
            builder.Append(clause.Field);
            builder.Append(clause.Ascending ? " ASC" : " DESC");
        }

        return builder.ToString();
    }
}
