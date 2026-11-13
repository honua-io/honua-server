// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Ai.Protocols.Mcp;

// Notification channels, lifetime tokens, and job indexes stay off this record because they back an SSE stream held open on one node.
internal sealed record McpSessionRecord(
    string SessionId,
    string PrincipalKey,
    bool ElicitationSupported,
    string? WorkflowView,
    DateTimeOffset CreatedUtc,
    int SchemaVersion = McpSessionRecord.CurrentSchemaVersion)
{
    public const int CurrentSchemaVersion = 1;
}

// The session record every node shares. A node learns that a session or request it holds work for
// has ended through PollAsync, which the session manager calls once a second while it has such work.
internal interface IMcpSessionStore : IMcpRequestStore
{
    Task SaveAsync(McpSessionRecord record, TimeSpan idleTimeout, CancellationToken cancellationToken);

    Task<McpSessionRecord?> GetAsync(string sessionId, CancellationToken cancellationToken);

    Task<bool> TouchAsync(string sessionId, TimeSpan idleTimeout, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(string sessionId, CancellationToken cancellationToken);

    Task RemoveAsync(string sessionId, CancellationToken cancellationToken);

    Task<McpPollResult> PollAsync(McpPollRequest request, CancellationToken cancellationToken);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(McpSessionRecord))]
internal sealed partial class McpSessionStoreJsonContext : JsonSerializerContext;
