// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Ai.Protocols.Mcp;

internal enum McpRequestOperation { Register, Read, Cancel, Finish }

internal enum McpRequestState { Missing, Active, Cancelled, Duplicate, Capacity }

internal interface IMcpRequestStore
{
    Task<McpRequestState> UpdateRequestAsync(
        string sessionId, string principalKey, string requestKey, string generation,
        McpRequestOperation operation, CancellationToken cancellationToken);
}

internal static class McpRequestBounds
{
    internal const int PerSession = 32;
    internal const int PerPrincipal = 128;
    internal const int PerInstance = 1024;
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
}
