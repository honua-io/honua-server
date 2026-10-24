// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Operations.Domain;
using Honua.Ai.Protocols.Mcp.Models;
using Microsoft.Extensions.Caching.Memory;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// Process-wide, param-keyed result cache for deterministic, read-only published
/// operations (#2483). Only invocations whose descriptor declares deterministic
/// (AI-free) execution with no side effect are cached: identical inputs then return
/// an identical result without re-executing the operation.
/// </summary>
/// <remarks>
/// <para>
/// The key is an opaque <c>sha256:</c> digest over the injective pre-image
/// <c>{operationId}|{catalogVersion}|{principalId}|{tier}|{tenant}|{schema}|{sortedRoles}|{scopeGoverned}|{sortedScopes}|{connectionId}|{serviceName}|{normalizedParameters}</c>.
/// A catalog change (a republished descriptor) invalidates prior entries by producing
/// a different key. The principal context, OAuth scopes, connection, and service are
/// part of the key. A hit still re-checks scope authority and, when one is composed,
/// the policy decision point, so a narrowed token is not served an earlier allow.
/// Side-effecting or AI-assisted operations are never cached. Entries expire and the
/// store is size-bounded.
/// </para>
/// <para>
/// The effective tenant and routed schema are part of the key for the same reason and
/// are load-bearing for #3430: this store is a process-wide singleton, and
/// <see cref="OperationPolicyContext.PrincipalId"/> is the canonical actor id, which is
/// deliberately NOT tenant-qualified. Without the tenant component, one multi-tenant
/// caller moving between tenants with an identical actor/tier/role set and identical
/// parameters would be served the other tenant's cached result — a cross-tenant read
/// that never reaches the policy decision point. The tenant component makes the two
/// invocations distinct keys, so each tenant takes its own policy round-trip.
/// </para>
/// <para>
/// Components are individually escaped and absent values carry a reserved
/// <c>none</c> tag distinct from any concrete <c>value:</c> form, so no combination of
/// delimiter-bearing tenants, schemas, roles, or parameter values can be re-parsed into
/// a different caller's key. The digest is returned to the caller as
/// <c>McpOperationToolOutput.CacheKey</c>, so the pre-image — which contains the routed
/// database schema and the caller's role set — is deliberately not emitted in plaintext.
/// </para>
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
    /// <see cref="OperationPolicyContext.Roles"/>) and to the effective tenant scope
    /// (<see cref="OperationPolicyContext.TenantId"/>,
    /// <see cref="OperationPolicyContext.SchemaName"/>), the caller's OAuth scopes,
    /// and the connection and service the request routes to.
    /// </summary>
    static string BuildKey(
        string operationId,
        string catalogVersion,
        IReadOnlyDictionary<string, string?> parameters,
        OperationPolicyContext principalContext,
        string? connectionId = null,
        string? serviceName = null)
    {
        ArgumentNullException.ThrowIfNull(principalContext);

        // Order-independent, null-safe normalization so identical inputs supplied in
        // any order produce the same key.
        var normalized = string.Join(
            ";",
            parameters
                .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                .Select(kvp => $"{Component(kvp.Key)}={Component(kvp.Value)}"));

        var roles = string.Join(
            ",",
            principalContext.Roles
                .OrderBy(role => role, StringComparer.Ordinal)
                .Select(Component));
        var scopes = string.Join(
            ",",
            principalContext.RecognizedScopes
                .OrderBy(scope => scope, StringComparer.Ordinal)
                .Select(Component));

        // Tenant and schema are as load-bearing as the principal: the cache is a
        // process-wide singleton and PrincipalId is not tenant-qualified. Scopes,
        // connection, and service are request fields the dispatcher authorizes on.
        var preImage = string.Join(
            "|",
            Component(operationId),
            Component(catalogVersion),
            Component(principalContext.PrincipalId),
            Component(principalContext.Tier),
            Component(principalContext.TenantId),
            Component(principalContext.SchemaName),
            roles,
            Component(principalContext.ScopeGoverned ? "governed" : "ungoverned"),
            scopes,
            Component(connectionId),
            Component(serviceName),
            normalized);

        // The caller receives this value, so the pre-image — which carries the routed
        // schema and role set — is never emitted in plaintext.
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(preImage)))}";
    }

    /// <summary>
    /// Encodes one key component so that no delimiter-bearing value can be re-parsed
    /// into another caller's key, and an absent value stays distinct from every
    /// concrete one.
    /// </summary>
    private static string Component(string? value) =>
        value is null ? "none" : $"value:{value.Length}:{Uri.EscapeDataString(value)}";
}

/// <inheritdoc />
internal sealed class PublishedOperationCache : IPublishedOperationCache, IDisposable
{
    /// <summary>Maximum retained deterministic results.</summary>
    internal const int MaxEntries = 256;

    /// <summary>Lifetime of one cached result.</summary>
    internal static readonly TimeSpan EntryTtl = TimeSpan.FromMinutes(5);

    private readonly MemoryCache _entries = new(new MemoryCacheOptions { SizeLimit = MaxEntries });

    /// <inheritdoc />
    public McpOperationToolOutput? TryGet(string key)
        => _entries.TryGetValue(key, out McpOperationToolOutput? cached) && cached is not null
            ? Clone(cached, cacheHit: true)
            : null;

    /// <inheritdoc />
    public void Set(string key, McpOperationToolOutput output)
        => _entries.Set(
            key,
            Clone(output, cacheHit: false),
            new MemoryCacheEntryOptions
            {
                Size = 1,
                AbsoluteExpirationRelativeToNow = EntryTtl,
            });

    /// <inheritdoc />
    public void Dispose() => _entries.Dispose();

    // Cache stores an immutable result snapshot. It never creates invocation identities;
    // the canonical envelope factory durably accepts every cache-hit invocation.
    private static McpOperationToolOutput Clone(McpOperationToolOutput source, bool cacheHit)
    {
        return new McpOperationToolOutput
        {
            Status = source.Status,
            RequiresApproval = source.RequiresApproval,
            Deterministic = source.Deterministic,
            CacheHit = cacheHit,
            CacheKey = source.CacheKey,
            OperationId = source.OperationId,
            OperationInstanceId = source.OperationInstanceId,
            HandleId = source.HandleId,
            ProposalId = source.ProposalId,
            CorrelationId = source.CorrelationId,
            AuditId = source.AuditId,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt,
            AuthorizationOutcome = source.AuthorizationOutcome,
            PolicyOutcome = source.PolicyOutcome,
            JobId = source.JobId,
            ApprovalLane = source.ApprovalLane,
            MetadataRevision = source.MetadataRevision,
            Summary = source.Summary,
            Message = source.Message,
            Details = new Dictionary<string, string>(source.Details, StringComparer.Ordinal),
            ResourceIds = new Dictionary<string, string>(source.ResourceIds, StringComparer.Ordinal),
            EvidenceRefs = [.. source.EvidenceRefs],
        };
    }
}
