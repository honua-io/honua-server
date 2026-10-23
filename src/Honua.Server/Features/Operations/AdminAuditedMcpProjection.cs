// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Services;

namespace Honua.Server.Features.Operations;

/// <summary>
/// The audited Admin MCP projection (honua-server#3363): the Admin API and access catalog
/// operations committed to <c>docs/gis/data/admin-mcp-projection-manifest.json</c>, minus the
/// audited exclusions. Other <c>admin.*</c> providers publish only with the full-catalog opt-in.
/// </summary>
internal sealed class AdminAuditedMcpProjection : IAuditedAdminMcpProjection
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> OperationIds { get; } = AdminApiOperationCatalog.Descriptors
        .Concat(AdminAccessOperationCatalog.Descriptors)
        .Select(descriptor => descriptor.OperationId)
        .Where(operationId => !AdminMcpOperationExclusions.ContainsOperation(operationId))
        .ToHashSet(StringComparer.Ordinal);
}
