// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Ai.Protocols.Mcp;

/// <summary>Atomic operations on a generation of an active transport request, never a durable job.</summary>
internal enum McpRequestOperation { Register, Read, Cancel, Finish }

/// <summary>Authoritative request state or an admission refusal.</summary>
internal enum McpRequestState { Missing, Active, Cancelled, Duplicate, Capacity }

/// <summary>
/// Session-owned request cancellation authority. Register/read/cancel must validate
/// the live session's principal; finish conditionally removes only its generation.
/// Cancel never creates an entry. All entries have a bounded lifetime and both
/// per-session and per-principal quotas, including across replicas.
/// </summary>
internal interface IMcpRequestStore
{
    Task<McpRequestState> UpdateRequestAsync(
        string sessionId, string principalKey, string requestKey, string generation,
        McpRequestOperation operation, CancellationToken cancellationToken);
}

/// <summary>Shared limits for the local and Redis implementations.</summary>
internal static class McpRequestBounds
{
    internal const int PerSession = 32;
    internal const int PerPrincipal = 128;
    internal const int PerInstance = 1024;
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
}
