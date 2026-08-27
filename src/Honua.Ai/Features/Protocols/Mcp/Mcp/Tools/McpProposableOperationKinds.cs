// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Guardrails.Domain;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// Operation classes the generic MCP proposal contract can express as validated,
/// resource-bound execution specifications. Both discovery and execution filter the
/// live gateway catalog through this set so advertised capabilities stay executable.
/// </summary>
internal static class McpProposableOperationKinds
{
    private static readonly HashSet<OperationClass> Supported =
    [
        OperationClass.Deploy,
        OperationClass.MetadataRelease,
    ];

    /// <summary>Returns whether the kind is safely representable by the generic MCP tool.</summary>
    public static bool Contains(OperationClass kind) => Supported.Contains(kind);
}
