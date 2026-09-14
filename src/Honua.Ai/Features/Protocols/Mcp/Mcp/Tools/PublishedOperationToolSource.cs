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
/// By default only the audited Admin projection publishes (#3363): operations named by a registered
/// <see cref="IAuditedAdminMcpProjection"/> (<c>Mcp:PublishOperations:AdminProjection</c>). The full
/// catalog publishes only when <c>Mcp:PublishOperations:Enabled</c> is set. Audited
/// <see cref="AdminMcpOperationExclusions"/> never publish. In "deterministic mode" (<c>DeterministicOnly</c>) only AI-free
/// descriptors are published — the audit/inspect toolset. Descriptors already exposed by a
/// hand-authored tool are skipped so the same operation is not advertised twice.
/// </remarks>
internal sealed class PublishedOperationToolSource : IMcpToolSource
{
    /// <summary>
    /// Operation ids already surfaced by a hand-authored MCP tool, so they are not
    /// double-published here. <c>service.publish</c> is <c>honua_publish_service</c> /
    /// <c>honua_publish_result</c>; <c>studio.content.create-publication-request</c> is
    /// <c>honua_studio_propose_publication</c>, which enforces the Studio owner authorization the
    /// generic projection does not.
    /// </summary>
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

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<IMcpTool>> GetToolsAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;
        var auditedOnly = !options.Enabled;
        if (auditedOnly && (!options.AdminProjection || _auditedOperationIds.Count == 0))
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

        return tools;
    }
}
