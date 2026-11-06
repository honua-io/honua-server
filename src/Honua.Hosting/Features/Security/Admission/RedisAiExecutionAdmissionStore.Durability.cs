// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using StackExchange.Redis;

namespace Honua.Infrastructure.Security.Admission;

internal sealed partial class RedisAiExecutionAdmissionStore
{
    private const CommandFlags AuthorityFlags = CommandFlags.DemandMaster | CommandFlags.NoRedirect;

    private async Task<RedisAuthority?> InspectAuthorityAsync(IDatabase database, RedisKey key, CancellationToken cancellationToken)
    {
        var endpoint = await database.IdentifyEndpointAsync(key, AuthorityFlags).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (endpoint is null)
        {
            return null;
        }
        var server = _connection.GetServer(endpoint);
        var serverInfo = server.InfoAsync("server");
        var replication = server.InfoAsync("replication");
        var persistence = server.InfoAsync("persistence");
        var appendOnly = server.ConfigGetAsync("appendonly");
        var fsync = server.ConfigGetAsync("appendfsync");
        var rewrite = server.ConfigGetAsync("no-appendfsync-on-rewrite");
        var eviction = server.ConfigGetAsync("maxmemory-policy");
        await Task.WhenAll(serverInfo, replication, persistence, appendOnly, fsync, rewrite, eviction)
            .WaitAsync(cancellationToken).ConfigureAwait(false);

        var serverValues = (await serverInfo.ConfigureAwait(false)).SelectMany(section => section).ToArray();
        var replicationValues = (await replication.ConfigureAwait(false)).SelectMany(section => section).ToArray();
        var persistenceValues = (await persistence.ConfigureAwait(false)).SelectMany(section => section).ToArray();
        if (!EqualsValue(await appendOnly.ConfigureAwait(false), "appendonly", "yes")
            || !EqualsValue(await fsync.ConfigureAwait(false), "appendfsync", "always")
            || !EqualsValue(await rewrite.ConfigureAwait(false), "no-appendfsync-on-rewrite", "no")
            || !EqualsValue(await eviction.ConfigureAwait(false), "maxmemory-policy", "noeviction")
            || !EqualsValue(persistenceValues, "aof_enabled", "1")
            || !EqualsValue(persistenceValues, "aof_last_write_status", "ok")
            || !EqualsValue(replicationValues, "role", "master"))
        {
            return null;
        }
        var runId = Value(serverValues, "run_id");
        var replicationId = Value(replicationValues, "master_replid");
        return IsRedisIdentity(runId) && IsRedisIdentity(replicationId) ? new(endpoint, runId!, replicationId!) : null;
    }

    private static bool IsRedisIdentity(string? value) => value is { Length: 40 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool EqualsValue(KeyValuePair<string, string>[] pairs, string name, string expected)
        => string.Equals(Value(pairs, name), expected, StringComparison.OrdinalIgnoreCase);
    private static string? Value(KeyValuePair<string, string>[] pairs, string name)
    {
        var matches = pairs.Where(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0].Value : null;
    }

    private sealed record RedisAuthority(EndPoint Endpoint, string RunId, string ReplicationId);
}
