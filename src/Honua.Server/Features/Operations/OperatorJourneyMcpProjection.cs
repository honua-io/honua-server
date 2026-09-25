// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;

namespace Honua.Server.Features.Operations;

/// <summary>
/// The closed operator MCP roster published beside the audited Admin API projection.
/// <c>Mcp:PublishOperations:Enabled</c> stays false; these four ids join the default projection.
/// </summary>
internal sealed class OperatorJourneyMcpProjection : IAuditedAdminMcpProjection
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> OperationIds { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "admin.server.status",
            "admin.connections.create",
            "admin.connections.test",
            "admin.import.upload-url",
        };
}
