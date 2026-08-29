// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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
/// <para>
/// The key is an opaque <c>sha256:</c> digest over the injective pre-image
/// <c>{operationId}|{catalogVersion}|{principalId}|{tier}|{tenant}|{schema}|{sortedRoles}|{normalizedParameters}</c>.
/// A catalog change (a republished descriptor) invalidates prior entries by producing
/// a different key. The full policy-relevant principal context (principal id, resolved
/// tier, sorted roles) is part of the key ON PURPOSE: the cache-hit path skips the
/// policy decision point, so a hit may only ever serve a result back to the identical
/// principal context that was already policy-allowed for those exact inputs — a
/// different caller, or the same caller with changed roles/tier, always misses and
/// takes a fresh policy round-trip. Side-effecting or AI-assisted operations are never
/// cached — a cache would either skip a side effect or return a stale AI turn — so
/// this cache is deliberately only consulted from the deterministic, read-only path.
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
    /// <see cref="OperationPolicyContext.SchemaName"/>) so a cached policy-allowed
    /// result can never be served across principals, tiers, role sets, or tenants.
    /// </summary>
    static string BuildKey(
        string operationId,
        string catalogVersion,
        IReadOnlyDictionary<string, string?> parameters,
        OperationPolicyContext principalContext)
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

        // Tenant and schema are as load-bearing as the principal: the cache is a
        // process-wide singleton and PrincipalId is not tenant-qualified.
        var preImage = string.Join(
            "|",
            Component(operationId),
            Component(catalogVersion),
            Component(principalContext.PrincipalId),
            Component(principalContext.Tier),
            Component(principalContext.TenantId),
            Component(principalContext.SchemaName),
            roles,
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
        string.IsNullOrWhiteSpace(value) ? "none" : $"value:{Uri.EscapeDataString(value)}";
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

    // Cache stores an immutable result snapshot. A cache hit is still a new invocation:
    // mint its envelope identities and retain the cached invocation only as evidence.
    private static McpOperationToolOutput Clone(McpOperationToolOutput source, bool cacheHit)
    {
        var operationInstanceId = cacheHit ? $"opinst-{Guid.NewGuid():N}" : source.OperationInstanceId;
        var correlationId = cacheHit ? $"corr-{Guid.NewGuid():N}" : source.CorrelationId;
        var now = DateTimeOffset.UtcNow;
        var evidenceRefs = cacheHit
            ? source.EvidenceRefs
                .Concat([$"cached-operation-instance:{source.OperationInstanceId}"])
                .Concat(string.IsNullOrWhiteSpace(source.AuditId) ? [] : [$"cached-audit:{source.AuditId}"])
                .ToArray()
            : [.. source.EvidenceRefs];

        return new McpOperationToolOutput
        {
            Status = source.Status,
            RequiresApproval = source.RequiresApproval,
            Deterministic = source.Deterministic,
            CacheHit = cacheHit,
            CacheKey = source.CacheKey,
            OperationId = source.OperationId,
            OperationInstanceId = operationInstanceId,
            HandleId = operationInstanceId,
            ProposalId = cacheHit ? null : source.ProposalId,
            CorrelationId = correlationId,
            AuditId = cacheHit ? null : source.AuditId,
            CreatedAt = cacheHit ? now : source.CreatedAt,
            UpdatedAt = cacheHit ? now : source.UpdatedAt,
            AuthorizationOutcome = source.AuthorizationOutcome,
            PolicyOutcome = source.PolicyOutcome,
            JobId = source.JobId,
            ApprovalLane = source.ApprovalLane,
            MetadataRevision = source.MetadataRevision,
            Summary = source.Summary,
            Message = source.Message,
            Details = new Dictionary<string, string>(source.Details, StringComparer.Ordinal),
            ResourceIds = new Dictionary<string, string>(source.ResourceIds, StringComparer.Ordinal),
            EvidenceRefs = evidenceRefs,
        };
    }
}
