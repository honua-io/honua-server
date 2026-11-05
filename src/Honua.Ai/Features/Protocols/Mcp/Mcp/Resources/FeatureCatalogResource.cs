// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using Honua.Core.Features.Authorization.Domain;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Resources;

internal sealed class FeatureCatalogResource : IMcpResource
{
    public const string Uri = McpResourceUris.CatalogFeatures;

    private const string EmbeddedResourceName = "Honua.Ai.Catalog.feature-catalog.json";

    private static readonly string CatalogJson = LoadEmbeddedCatalog();

    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<FeatureCatalogResource> _logger;

    public FeatureCatalogResource(
        IGeoprocessingJobService jobService,
        ILogger<FeatureCatalogResource> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    public string Family => McpTelemetry.ResourceFamily.Catalog;

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

    public IReadOnlyList<McpResourceTemplateDescriptor> DescribeTemplates() => [];

    public bool CanHandle(string uri) => string.Equals(uri, Uri, StringComparison.Ordinal);

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
