// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Federation.Domain;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Admin.Federation.Connectors;

/// <summary>
/// HTTP <see cref="HttpGeoJsonFederatedSourceConnector"/> for OGC API - Features / WFS sources.
/// It fetches GeoJSON items from <c>{endpoint}/collections/{remoteLayer}/items</c>, pushing
/// down the predicates the planner allows for the <see cref="FederatedSourceKind.OgcWfs"/>
/// transport: a CQL2-text attribute <c>filter</c>, a <c>bbox</c> envelope, and offset/limit
/// paging. Ordering and exact spatial relationships are refined locally by the federation
/// executor, so this connector never emits them remotely (issue #341).
/// </summary>
internal sealed class OgcFeaturesFederatedSourceConnector : HttpGeoJsonFederatedSourceConnector
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OgcFeaturesFederatedSourceConnector"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Factory for the named federation HTTP client.</param>
    /// <param name="logger">Logger for transport diagnostics.</param>
    public OgcFeaturesFederatedSourceConnector(
        IHttpClientFactory httpClientFactory,
        ILogger<OgcFeaturesFederatedSourceConnector> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override FederatedSourceKind Kind => FederatedSourceKind.OgcWfs;

    /// <inheritdoc />
    protected override Uri BuildRequestUri(FederatedFetchRequest request)
    {
        var source = request.Source;
        var query = request.Query;

        var basePath = source.Endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var itemsUrl = $"{basePath}/collections/{Uri.EscapeDataString(source.RemoteLayer)}/items";

        var parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["f"] = "json",
        };

        if (request.ShouldPushDown(FederationPredicateKind.AttributeFilter) &&
            !string.IsNullOrWhiteSpace(query.Where))
        {
            // The planner pushes the attribute filter down for OGC sources; simple comparison
            // predicates are compatible between the canonical WHERE syntax and CQL2-text.
            parameters["filter"] = query.Where;
            parameters["filter-lang"] = "cql2-text";
        }

        if (TryGetPushedDownEnvelope(request, out var envelope))
        {
            parameters["bbox"] = string.Join(
                ',',
                Invariant(envelope.MinX),
                Invariant(envelope.MinY),
                Invariant(envelope.MaxX),
                Invariant(envelope.MaxY));
        }

        if (request.ShouldPushDown(FederationPredicateKind.Paging))
        {
            if (query.Limit is { } limit && limit > 0)
            {
                parameters["limit"] = limit.ToString(CultureInfo.InvariantCulture);
            }

            if (query.Offset is { } offset && offset > 0)
            {
                parameters["offset"] = offset.ToString(CultureInfo.InvariantCulture);
            }
        }

        return new Uri(QueryHelpers.AddQueryString(itemsUrl, parameters));
    }
}
