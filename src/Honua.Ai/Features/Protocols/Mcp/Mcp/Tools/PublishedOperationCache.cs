// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Honua.Core.Features.Operations.Domain;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// Process-wide, param-keyed result cache for deterministic, read-only published
/// operations (#2483). Only invocations whose descriptor declares deterministic
/// (AI-free) execution with no side effect are cached: identical inputs then return
/// an identical result without re-executing the operation.
/// </summary>
/// <remarks>
/// The key is a SHA-256 digest of a canonical JSON envelope containing the operation,
/// catalog, actor/tenant authorization context, and sorted parameters/roles/permissions.
/// A catalog change (a republished descriptor) invalidates prior entries by producing
/// a different key. The full policy-relevant principal context (principal id, resolved
/// tier, tenant, sorted roles, and sorted permissions) is part of the key ON PURPOSE:
/// the cache-hit path skips the
/// policy decision point, so a hit may only ever serve a result back to the identical
/// principal context that was already policy-allowed for those exact inputs — a
/// different caller, or the same caller with changed roles/tier, always misses and
/// takes a fresh policy round-trip. Side-effecting or AI-assisted operations are never
/// cached — a cache would either skip a side effect or return a stale AI turn — so
/// this cache is deliberately only consulted from the deterministic, read-only path.
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
    /// The key binds the result to the invoking principal context
    /// (<see cref="OperationPolicyContext.PrincipalId"/>,
    /// <see cref="OperationPolicyContext.Tier"/>, sorted
    /// <see cref="OperationPolicyContext.Roles"/>) so a cached policy-allowed result
    /// can never be served across principals, tiers, or role sets.
    /// </summary>
    static string BuildKey(
        string operationId,
        string catalogVersion,
        IReadOnlyDictionary<string, string?> parameters,
        OperationPolicyContext principalContext)
    {
        ArgumentNullException.ThrowIfNull(principalContext);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("operationId", operationId);
            writer.WriteString("catalogVersion", catalogVersion);
            writer.WriteString("principalId", NormalizeIdentity(principalContext.PrincipalId));
            writer.WriteString("tier", Normalize(principalContext.Tier));
            writer.WriteString("tenantId", Normalize(principalContext.TenantId));
            WriteNormalizedArray(writer, "roles", principalContext.Roles);
            WriteNormalizedArray(writer, "permissions", principalContext.Permissions);
            writer.WritePropertyName("parameters");
            writer.WriteStartObject();
            foreach (var parameter in parameters.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
            {
                if (parameter.Value is null)
                {
                    writer.WriteNull(parameter.Key);
                }
                else
                {
                    writer.WriteString(parameter.Key, parameter.Value);
                }
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        var digest = SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
        return $"mcpop:v1:{Convert.ToHexStringLower(digest)}";
    }

    private static void WriteNormalizedArray(
        Utf8JsonWriter writer,
        string propertyName,
        IEnumerable<string> values)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var value in values
                     .Select(Normalize)
                     .Where(static value => value.Length > 0)
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(static value => value, StringComparer.Ordinal))
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static string NormalizeIdentity(string? value) => value?.Trim() ?? string.Empty;

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;
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
