// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using Honua.Core.Features.Authorization.Domain;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Resources;

/// <summary>
/// MCP resource for <c>honua://catalog/features</c>. Serves the evidence-based
/// feature catalog (#1946, ADR-0054): a generated, drift-gated projection of the
/// shipped HTTP API surface where every entry is backed by a registry route, at
/// least one <c>[Endpoint]</c>-attributed proving test, and a proof-ledger
/// surface.
/// </summary>
/// <remarks>
/// The committed, drift-gated artifact (<c>docs/gis/data/feature-catalog.json</c>)
/// is embedded into this assembly (logical name
/// <c>Honua.Ai.Catalog.feature-catalog.json</c>) so the resource serves it from a
/// deployed server that has no repo checkout. The artifact is generated, never
/// authored — <c>FeatureCatalogDriftTests</c> in <c>Honua.Architecture.Tests</c>
/// fails the build if the committed file drifts from the live API surface, so
/// what this resource serves can never be a hand-edited or stale claim. Reading
/// the embedded text verbatim (rather than re-projecting at runtime) keeps the
/// resource AOT-safe and guarantees it serves exactly what CI gated.
/// </remarks>
internal sealed class FeatureCatalogResource : IMcpResource
{
    /// <summary>Canonical URI this resource handles.</summary>
    public const string Uri = McpResourceUris.CatalogFeatures;

    private const string EmbeddedResourceName = "Honua.Ai.Catalog.feature-catalog.json";

    private static readonly string CatalogJson = LoadEmbeddedCatalog();

    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<FeatureCatalogResource> _logger;

    /// <summary>Initializes a new instance of the <see cref="FeatureCatalogResource"/> class.</summary>
    public FeatureCatalogResource(
        IGeoprocessingJobService jobService,
        ILogger<FeatureCatalogResource> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    /// <summary>Telemetry family tag for catalog resources.</summary>
    public string Family => McpTelemetry.ResourceFamily.Catalog;

    /// <summary>Advertises the fixed feature-catalog URI via <c>resources/list</c>.</summary>
    public IReadOnlyList<McpResourceDescriptor> Describe() => new[]
    {
        new McpResourceDescriptor
        {
            Uri = Uri,
            Name = "Feature catalog",
            Description =
                "Evidence-based catalog of the server's HTTP API surface (#1946): every entry "
                + "is a registered route backed by proving tests and a proof-ledger surface.",
            MimeType = McpResourceHelpers.JsonMimeType
        }
    };

    /// <summary>This resource has a fixed URI, so it advertises no templates.</summary>
    public IReadOnlyList<McpResourceTemplateDescriptor> DescribeTemplates() => [];

    /// <summary>Returns <c>true</c> only for the exact feature-catalog URI.</summary>
    public bool CanHandle(string uri) => string.Equals(uri, Uri, StringComparison.Ordinal);

    /// <summary>Authorizes the caller and serves the embedded, drift-gated catalog.</summary>
    public async Task<McpResourcesReadResult> ReadAsync(
        HttpContext httpContext,
        string uri,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("GetFeatureCatalog");
        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        await _jobService
            .EnsureCallerAuthorizedAsync(principal, OperatorResourceType.Catalog, OperatorOperation.Discover, cancellationToken)
            .ConfigureAwait(false);
        McpLog.ResourceRead(_logger, Family, uri);

        return new McpResourcesReadResult
        {
            Contents =
            [
                new McpResourceContent
                {
                    Uri = uri,
                    MimeType = McpResourceHelpers.JsonMimeType,
                    Text = CatalogJson
                }
            ]
        };
    }

    private static string LoadEmbeddedCatalog()
    {
        var assembly = typeof(FeatureCatalogResource).Assembly;
        using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded feature catalog '{EmbeddedResourceName}' was not found in {assembly.GetName().Name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
