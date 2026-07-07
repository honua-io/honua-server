// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// Process-wide, param-keyed result cache for deterministic, read-only published
/// operations (#2483). Only invocations whose descriptor declares deterministic
/// (AI-free) execution with no side effect are cached: identical inputs then return
/// an identical result without re-executing the operation.
/// </summary>
/// <remarks>
/// The key is <c>{operationId}|{catalogVersion}|{normalizedParameters}</c>, so a
/// catalog change (a republished descriptor) invalidates prior entries by producing
/// a different key. Side-effecting or AI-assisted operations are never cached — a
/// cache would either skip a side effect or return a stale AI turn — so this cache
/// is deliberately only consulted from the deterministic, read-only path.
/// </remarks>
internal interface IPublishedOperationCache
{
    /// <summary>
    /// Returns the cached output for <paramref name="key"/>, or <see langword="null"/>
    /// on a miss. The returned instance is a fresh copy flagged <c>cacheHit=true</c>.
    /// </summary>
    McpOperationToolOutput? TryGet(string key);

    /// <summary>Stores <paramref name="output"/> under <paramref name="key"/>.</summary>
    void Set(string key, McpOperationToolOutput output);

    /// <summary>
    /// Builds the param-keyed cache key for a deterministic, read-only invocation.
    /// </summary>
    static string BuildKey(
        string operationId,
        string catalogVersion,
        IReadOnlyDictionary<string, string?> parameters)
    {
        // Order-independent, null-safe normalization so identical inputs supplied in
        // any order produce the same key.
        var normalized = string.Join(
            ";",
            parameters
                .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                .Select(kvp => $"{kvp.Key}={kvp.Value}"));
        return $"{operationId}|{catalogVersion}|{normalized}";
    }
}

/// <inheritdoc />
internal sealed class PublishedOperationCache : IPublishedOperationCache
{
    private readonly ConcurrentDictionary<string, McpOperationToolOutput> _entries =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public McpOperationToolOutput? TryGet(string key)
        => _entries.TryGetValue(key, out var cached) ? Clone(cached, cacheHit: true) : null;

    /// <inheritdoc />
    public void Set(string key, McpOperationToolOutput output)
        => _entries[key] = Clone(output, cacheHit: false);

    // Cache stores an immutable snapshot; every read/write returns a fresh instance so
    // a caller mutating its output can never corrupt a cached entry.
    private static McpOperationToolOutput Clone(McpOperationToolOutput source, bool cacheHit) => new()
    {
        Status = source.Status,
        RequiresApproval = source.RequiresApproval,
        Deterministic = source.Deterministic,
        CacheHit = cacheHit,
        CacheKey = source.CacheKey,
        OperationId = source.OperationId,
        HandleId = source.HandleId,
        JobId = source.JobId,
        ApprovalLane = source.ApprovalLane,
        MetadataRevision = source.MetadataRevision,
        Summary = source.Summary,
        Message = source.Message,
        Details = new Dictionary<string, string>(source.Details, StringComparer.Ordinal),
    };
}
