// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Ai.Protocols.Mcp;

// logging/setLevel state. With a shared store the level lives on the session record, so a
// level set through one server task holds for notifications another task sends.
internal sealed partial class McpSessionManager
{
    public async Task<bool> SetLogLevelAsync(string sessionId, string level, CancellationToken cancellationToken)
    {
        var known = false;
        if (!string.IsNullOrEmpty(sessionId) && _sessions.TryGetValue(sessionId, out var session))
        {
            session.LogLevel = level;
            known = true;
        }

        if (_store is null)
        {
            return known;
        }

        try
        {
            return await _store.SetLogLevelAsync(sessionId, level, cancellationToken).ConfigureAwait(false) || known;
        }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            LogStoreFailure(ex, "set-log-level");
            return known;
        }
    }

    public async Task<string?> GetLogLevelAsync(string sessionId, CancellationToken cancellationToken)
    {
        var local = !string.IsNullOrEmpty(sessionId) && _sessions.TryGetValue(sessionId, out var session)
            ? session.LogLevel
            : null;
        if (_store is null)
        {
            return local;
        }

        try
        {
            var record = await _store.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return record?.LogLevel ?? local;
        }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            LogStoreFailure(ex, "get-log-level");
            return local;
        }
    }
}
