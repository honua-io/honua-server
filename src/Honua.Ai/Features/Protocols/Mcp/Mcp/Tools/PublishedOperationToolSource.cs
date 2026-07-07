// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// Projects validated operations-toolset descriptors from the <see cref="IOperationCatalog"/>
/// into first-class, typed, policy-governed MCP tools (#2483, ADR-0056 Increment 4).
/// A descriptor present in the catalog is "published"; this source turns each one into a
/// <see cref="PublishedOperationTool"/> that the <see cref="McpOperatorSurface"/> merges into
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
    private readonly IOptions<McpPublishedOperationOptions> _options;
    private readonly ILogger<PublishedOperationToolSource> _logger;

    public PublishedOperationToolSource(
        IOperationCatalog catalog,
        IOptions<McpPublishedOperationOptions> options,
        ILogger<PublishedOperationToolSource> logger)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
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

        var tools = new List<IMcpTool>(snapshot.Operations.Count);
        foreach (var descriptor in snapshot.Operations)
        {
            if (ExcludedOperationIds.Contains(descriptor.OperationId))
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
