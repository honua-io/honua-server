// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using StackExchange.Redis;

namespace Honua.Server.Features.AgentWorkspace;

/// <summary>Durable shared workspace state. No process-memory fallback or expiring command journal.</summary>
internal interface IAgentWorkspaceStore
{
    bool IsAvailable { get; }
    Task<string> GetTargetIdAsync();
    Task<AgentWorkspaceDocument?> GetAsync(string id);
    Task<bool> CreateAsync(AgentWorkspaceDocument document);
    Task<bool> SaveAsync(AgentWorkspaceDocument document);
}

internal sealed class AgentWorkspaceStore(IConnectionMultiplexer? redis, string? deploymentId) : IAgentWorkspaceStore
{
    private const string CompareAndSet = """
        local current = redis.call('GET', KEYS[1])
        if current == false then return 0 end
        if tonumber(cjson.decode(current).version) ~= tonumber(ARGV[1]) then return 0 end
        redis.call('SET', KEYS[1], ARGV[2])
        return 1
        """;
    private readonly string? _prefix = AgentWorkspaceState.IsIdentifier(deploymentId)
        ? $"agent-workspace:{deploymentId}" : null;
    public bool IsAvailable => redis is not null && _prefix is not null;

    private IDatabase Database => (IsAvailable ? redis!.GetDatabase() : null)
        ?? throw new AgentWorkspaceUnavailableException();

    public async Task<string> GetTargetIdAsync()
    {
        var key = $"{_prefix}:target";
        var database = Database;
        await database.StringSetAsync(key, $"target-{Guid.NewGuid():N}", when: When.NotExists).ConfigureAwait(false);
        var target = await database.StringGetAsync(key).ConfigureAwait(false);
        return target.HasValue ? target.ToString() : throw new AgentWorkspaceUnavailableException();
    }

    public async Task<AgentWorkspaceDocument?> GetAsync(string id)
    {
        var json = await Database.StringGetAsync(Key(id)).ConfigureAwait(false);
        return json.HasValue
            ? JsonSerializer.Deserialize(json.ToString(), AgentWorkspaceJsonContext.Default.AgentWorkspaceDocument)
            : null;
    }

    public Task<bool> CreateAsync(AgentWorkspaceDocument document)
        => Database.StringSetAsync(Key(document.WorkspaceId),
            JsonSerializer.Serialize(document, AgentWorkspaceJsonContext.Default.AgentWorkspaceDocument), when: When.NotExists);

    public async Task<bool> SaveAsync(AgentWorkspaceDocument document)
    {
        var result = await Database.ScriptEvaluateAsync(CompareAndSet,
            [(RedisKey)Key(document.WorkspaceId)],
            [(RedisValue)document.Version, (RedisValue)JsonSerializer.Serialize(document with { Version = document.Version + 1 },
                AgentWorkspaceJsonContext.Default.AgentWorkspaceDocument)]).ConfigureAwait(false);
        return (int)result == 1;
    }

    private string Key(string id) => $"{_prefix}:document:{id}";
}
internal sealed class AgentWorkspaceUnavailableException : Exception;
