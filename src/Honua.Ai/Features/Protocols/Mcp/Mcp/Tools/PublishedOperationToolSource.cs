// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.ObjectModel;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Ai.Protocols.Mcp.Tools;

internal sealed class PublishedOperationToolSource : IMcpToolSource
{
    // studio.content.create-publication-request is excluded because honua_studio_propose_publication enforces Studio owner authorization the generic projection does not.
    private static readonly HashSet<string> ExcludedOperationIds =
        new(StringComparer.Ordinal)
        {
            PublishServiceTool.PublishOperationId,
            "style.apply-preset",
            "studio.content.create-publication-request",
        };

    private static readonly IReadOnlyList<IMcpTool> Empty = [];

    private readonly IOperationCatalog _catalog;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly IOptions<McpPublishedOperationOptions> _options;
    private readonly ILogger<PublishedOperationToolSource> _logger;
    private readonly IReadOnlyDictionary<string, int> _mapperCounts;
    private readonly HashSet<string> _auditedOperationIds;
    private volatile CachedProjection? _cached;

    public PublishedOperationToolSource(
        IOperationCatalog catalog,
        IOptions<McpPublishedOperationOptions> options,
        ILogger<PublishedOperationToolSource> logger,
        IServiceScopeFactory? scopeFactory = null,
        IEnumerable<IOperationApprovalRequestMapper>? requestMappers = null,
        IEnumerable<IAuditedAdminMcpProjection>? auditedProjections = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _scopeFactory = scopeFactory;
        _mapperCounts = OperationDescriptorPublication.CountMappings(requestMappers ?? []);
        _auditedOperationIds = (auditedProjections ?? [])
            .SelectMany(projection => projection.OperationIds)
            .ToHashSet(StringComparer.Ordinal);
    }

    public async ValueTask<IReadOnlyList<IMcpTool>> GetToolsAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;
        var auditedOnly = !options.Enabled;
        if (auditedOnly && (!options.AdminProjection || _auditedOperationIds.Count == 0))
        {
            return Empty;
        }

        var snapshot = await _catalog.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var cached = _cached;
        if (cached is not null && cached.Matches(snapshot, options))
        {
            return cached.Tools;
        }

        var tools = Project(snapshot, options, auditedOnly);
        _cached = new CachedProjection(
            snapshot.CatalogVersion,
            snapshot.Operations,
            options.Enabled,
            options.DeterministicOnly,
            tools);
        return tools;
    }

    private ReadOnlyCollection<IMcpTool> Project(
        OperationCatalogSnapshot snapshot,
        McpPublishedOperationOptions options,
        bool auditedOnly)
    {
        HashSet<string>? executorOperationIds = null;
        if (_scopeFactory is not null)
        {
            using var scope = _scopeFactory.CreateScope();
            executorOperationIds = scope.ServiceProvider
                .GetServices<IOperationExecutor>()
                .Select(e => e.OperationId)
                .ToHashSet(StringComparer.Ordinal);
        }

        var tools = new List<IMcpTool>(snapshot.Operations.Count);
        foreach (var descriptor in snapshot.Operations)
        {
            // Without the full-catalog opt-in, only the audited Admin projection publishes.
            if (auditedOnly && !_auditedOperationIds.Contains(descriptor.OperationId))
            {
                continue;
            }
            if (!OperationDescriptorPublication.CanAdvertise(descriptor, _mapperCounts))
            {
                continue;
            }
            if (executorOperationIds is not null && !executorOperationIds.Contains(descriptor.OperationId))
            {
                continue;
            }
            if (ExcludedOperationIds.Contains(descriptor.OperationId))
            {
                continue;
            }
            if (AdminMcpOperationExclusions.ContainsOperation(descriptor.OperationId))
            {
                continue;
            }

            // Deterministic mode: only publish AI-free descriptors.
            if (options.DeterministicOnly
                && descriptor.Policy.Determinism != OperationDeterminism.Deterministic)
            {
                continue;
            }

            tools.Add(new PublishedOperationTool(descriptor, snapshot.CatalogVersion, _logger));
        }

        return tools.AsReadOnly();
    }

    // Descriptor instances are compared as well as the catalog version, because that version hashes only operation and provider ids and would miss a schema or policy change.
    private sealed record CachedProjection(
        string CatalogVersion,
        IReadOnlyList<OperationDescriptor> Operations,
        bool Enabled,
        bool DeterministicOnly,
        IReadOnlyList<IMcpTool> Tools)
    {
        public bool Matches(OperationCatalogSnapshot snapshot, McpPublishedOperationOptions options)
        {
            if (!string.Equals(CatalogVersion, snapshot.CatalogVersion, StringComparison.Ordinal)
                || Enabled != options.Enabled
                || DeterministicOnly != options.DeterministicOnly
                || Operations.Count != snapshot.Operations.Count)
            {
                return false;
            }

            for (var i = 0; i < Operations.Count; i++)
            {
                if (!ReferenceEquals(Operations[i], snapshot.Operations[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
