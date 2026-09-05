// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Guardrails.Domain;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>Operation classes represented by the bounded generic MCP proposal contract.</summary>
internal static class McpProposableOperationKinds
{
    private static readonly HashSet<OperationClass> Supported =
    [
        OperationClass.Deploy,
        OperationClass.MetadataRelease,
    ];

    public static bool Contains(OperationClass kind) => Supported.Contains(kind);
}
