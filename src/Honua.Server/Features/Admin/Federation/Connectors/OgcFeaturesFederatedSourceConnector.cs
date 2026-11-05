// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Federation.Domain;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Admin.Federation.Connectors;

// Ordering and exact spatial relationships are refined locally, so this connector never sends them to the remote service.
internal sealed class OgcFeaturesFederatedSourceConnector : HttpGeoJsonFederatedSourceConnector
{
    public OgcFeaturesFederatedSourceConnector(
        IHttpClientFactory httpClientFactory,
        ILogger<OgcFeaturesFederatedSourceConnector> logger)
        : base(httpClientFactory, logger)
    {
    }

    public override FederatedSourceKind Kind => FederatedSourceKind.OgcWfs;

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
