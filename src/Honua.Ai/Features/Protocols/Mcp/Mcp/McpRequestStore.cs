// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Ai.Protocols.Mcp;

internal enum McpRequestOperation { Register, Read, Cancel, Finish }

internal enum McpRequestState { Missing, Active, Cancelled, Duplicate, Capacity }

internal interface IMcpRequestStore
{
    Task<McpRequestState> UpdateRequestAsync(
        McpActiveRequest request, McpRequestOperation operation, CancellationToken cancellationToken);
}

// One request this node is running, identified down to its generation.
internal sealed record McpActiveRequest(string SessionId, string PrincipalKey, string RequestKey, string Generation);

// The sessions this node holds streams or work for, and the requests it is running.
internal sealed record McpPollRequest(IReadOnlyCollection<string> SessionIds, IReadOnlyCollection<McpActiveRequest> Requests);

// The sessions that no longer exist and the requests that are no longer active (cancelled, finished elsewhere or expired).
internal sealed record McpPollResult(IReadOnlyCollection<string> EndedSessions, IReadOnlyCollection<McpActiveRequest> EndedRequests)
{
    public static readonly McpPollResult Nothing = new([], []);
}

internal static class McpRequestBounds
{
    internal const int PerSession = 32;
    internal const int PerPrincipal = 128;
    internal const int PerInstance = 1024;
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
}

internal static class McpRequestPoll
{
    // The request half of a poll answered one read at a time, for a store that keeps requests in process.
    public static async Task<IReadOnlyCollection<McpActiveRequest>> ReadEachAsync(
        IMcpRequestStore store, IReadOnlyCollection<McpActiveRequest> requests, CancellationToken cancellationToken)
    {
        var ended = new List<McpActiveRequest>();
        foreach (var request in requests)
        {
            var state = await store.UpdateRequestAsync(request, McpRequestOperation.Read, cancellationToken).ConfigureAwait(false);
            if (state != McpRequestState.Active)
            {
                ended.Add(request);
            }
        }

        return ended;
    }
}
