// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// Projects validated operations-toolset descriptors from the <see cref="IOperationCatalog"/>
/// into first-class, typed, policy-governed MCP tools (#2483, ADR-0056 Increment 4).
/// A descriptor present in the catalog is "published"; this source turns each one into a
/// <see cref="PublishedOperationTool"/> that the <see cref="McpDataAccessSurface"/> merges into
/// <c>tools/list</c> and <c>tools/call</c>.
/// </summary>
/// <remarks>
/// Off unless <c>Mcp:PublishOperations:Enabled</c> is set, so no host changes its advertised
/// catalog by default. In "deterministic mode" (<c>DeterministicOnly</c>) only AI-free
/// descriptors are published — the audit/inspect toolset. Descriptors already exposed by a
/// hand-authored tool are skipped so the same operation is not advertised twice.
/// </remarks>
internal sealed class PublishedOperationToolSource : IMcpToolSource
{
    /// <summary>
    /// Operation ids already surfaced by a hand-authored MCP tool, so they are not
    /// double-published here. <c>service.publish</c> is <c>honua_publish_service</c> /
    /// <c>honua_publish_result</c>.
    /// </summary>
    private static readonly HashSet<string> ExcludedOperationIds =
        new(StringComparer.Ordinal) { PublishServiceTool.PublishOperationId };

    private static readonly IReadOnlyList<IMcpTool> Empty = [];

    private readonly IOperationCatalog _catalog;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly IOptions<McpPublishedOperationOptions> _options;
    private readonly ILogger<PublishedOperationToolSource> _logger;
    private readonly IReadOnlyDictionary<string, int> _mapperCounts;

    public PublishedOperationToolSource(
        IOperationCatalog catalog,
        IOptions<McpPublishedOperationOptions> options,
        ILogger<PublishedOperationToolSource> logger,
        IServiceScopeFactory? scopeFactory = null,
        IEnumerable<IOperationApprovalRequestMapper>? requestMappers = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _scopeFactory = scopeFactory;
        _mapperCounts = OperationDescriptorPublication.CountMappings(requestMappers ?? []);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<IMcpTool>> GetToolsAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            return Empty;
        }

        var snapshot = await _catalog.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
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

        return tools;
    }
}
