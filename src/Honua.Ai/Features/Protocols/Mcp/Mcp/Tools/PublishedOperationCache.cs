// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Operations.Domain;
using Honua.Ai.Protocols.Mcp.Models;
using Microsoft.Extensions.Caching.Memory;

namespace Honua.Ai.Protocols.Mcp.Tools;

// The actor id is not tenant-qualified, so the tenant is part of the key, and the digest is returned instead of a pre-image that contains the schema and roles.
internal interface IPublishedOperationCache
{
    McpOperationToolOutput? TryGet(string key);

    void Set(string key, McpOperationToolOutput output);

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

    private static string Component(string? value) =>
        value is null ? "none" : $"value:{value.Length}:{Uri.EscapeDataString(value)}";
}

internal sealed class PublishedOperationCache : IPublishedOperationCache, IDisposable
{
    internal const int MaxEntries = 256;

    internal static readonly TimeSpan EntryTtl = TimeSpan.FromMinutes(5);

    private readonly MemoryCache _entries = new(new MemoryCacheOptions { SizeLimit = MaxEntries });

    public McpOperationToolOutput? TryGet(string key)
        => _entries.TryGetValue(key, out McpOperationToolOutput? cached) && cached is not null
            ? Clone(cached, cacheHit: true)
            : null;

    public void Set(string key, McpOperationToolOutput output)
        => _entries.Set(
            key,
            Clone(output, cacheHit: false),
            new MemoryCacheEntryOptions
            {
                Size = 1,
                AbsoluteExpirationRelativeToNow = EntryTtl,
            });

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
