// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Honua.Core.Features.Operations.Domain;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.Operations;

internal static class OperationLineageHeaders
{
    internal const string Attestation = "X-Honua-Lineage-Attestation";
    internal const string OperationInstanceId = "X-Honua-Operation-Instance-Id";
    internal const string AuditId = "X-Honua-Audit-Id";
    internal const string ProposalId = "X-Honua-Proposal-Id";
    internal const string CorrelationId = "X-Correlation-ID";

    // The loopback hop forwards the caller's own credential, so the attestation carries whether the
    // caller was an agent; a human-approved replay runs on a server-minted credential and is not marked.
    internal static void Apply(
        HttpRequestMessage message,
        OperationLoopbackHop hop,
        OperationLineageAttestationStore attestationStore)
    {
        var context = hop.Policy;
        Add(message, OperationInstanceId, context.OperationInstanceId);
        Add(message, CorrelationId, context.CorrelationId);
        Add(message, AuditId, context.AuditId);
        Add(message, ProposalId, context.ProposalId ?? context.ApprovedProposalId);
        Add(message, Attestation, attestationStore.Issue(OperationLineageAttestationStore.Describe(context) with
        {
            Admission = hop.Admission,
            AgentOrigin = string.IsNullOrWhiteSpace(context.ApprovedProposalId)
                && Honua.Infrastructure.Authentication.AgentPrincipal.IsAgentRequest(hop.Origin),
            AgentRequested = Honua.Infrastructure.Authentication.AgentPrincipal.IsAgentRequest(hop.Origin)
                || Honua.Core.Features.Operations.Policy.AgentApprovalRule.IsAgent(context.Roles),
        }));
    }

    private static void Add(HttpRequestMessage message, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }
    }
}

// One loopback dispatch: the policy context, the request it is made for, and an optional rate-limit admission.
internal sealed record OperationLoopbackHop(OperationPolicyContext Policy, HttpContext Origin)
{
    public OperationLoopbackAdmission? Admission { get; init; }
}

internal sealed class OperationLineageAttestationStore(TimeProvider clock)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Entry> _pending =
        new(StringComparer.Ordinal);

    internal static OperationLineageAttestation Describe(OperationPolicyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new OperationLineageAttestation
        {
            OperationInstanceId = context.OperationInstanceId,
            AuditId = context.AuditId,
            ProposalId = context.ProposalId ?? context.ApprovedProposalId,
        };
    }

    internal string Issue(OperationPolicyContext context, OperationLoopbackAdmission? admission = null)
        => Issue(Describe(context) with { Admission = admission });

    internal string Issue(OperationLineageAttestation attestation)
    {
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        _pending[token] = new Entry(attestation, clock.GetUtcNow().Add(Lifetime));
        return token;
    }

    internal bool TryConsume(string token, out OperationLineageAttestation value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(token) ||
            !_pending.TryRemove(token, out var entry) ||
            entry.ExpiresAt <= clock.GetUtcNow())
        {
            return false;
        }

        value = entry.Value;
        return true;
    }

    private sealed record Entry(OperationLineageAttestation Value, DateTimeOffset ExpiresAt);
}

internal readonly record struct OperationLineageAttestation
{
    public string? OperationInstanceId { get; init; }

    public string? AuditId { get; init; }

    public string? ProposalId { get; init; }

    public OperationLoopbackAdmission? Admission { get; init; }

    // The dispatching caller was an agent; the receiving hop marks its principal as one.
    public bool AgentOrigin { get; init; }

    // An agent made the call, or made the proposal a human approved (the replay carries the agent
    // role but is not marked, so approval gates do not apply twice).
    public bool AgentRequested { get; init; }
}

// Set on a request that carried a valid one-use attestation: the operations gateway dispatched
// it after the operation policy decided.
internal sealed class AttestedOperationLineage
{
    internal static readonly AttestedOperationLineage Instance = new();

    internal static readonly AttestedOperationLineage ForAgent = new() { AgentRequested = true };

    private AttestedOperationLineage()
    {
    }

    // A credential issued on this hop is an agent credential.
    internal bool AgentRequested { get; private init; }
}

internal sealed record OperationLoopbackAdmission(
    Guid CredentialId, string Method, string Path, string? TenantId)
{
    internal bool Matches(HttpContext context)
        => context.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote) &&
           context.User.Identity?.IsAuthenticated == true &&
           Guid.TryParse(context.User.FindFirst("api_key_id")?.Value, out var credential) &&
           credential == CredentialId &&
           string.Equals(context.Request.Method, Method, StringComparison.Ordinal) &&
           string.Equals(context.Request.Path.Value, Path, StringComparison.Ordinal);
}

internal sealed class OperationLineageAttestationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, OperationLineageAttestationStore store)
    {
        var token = context.Request.Headers[OperationLineageHeaders.Attestation].ToString();
        context.Features.Set<OperationLoopbackAdmission>(null);
        context.Features.Set<AttestedOperationLineage>(null);

        context.Request.Headers.Remove(OperationLineageHeaders.Attestation);
        context.Request.Headers.Remove(OperationLineageHeaders.OperationInstanceId);
        context.Request.Headers.Remove(OperationLineageHeaders.AuditId);
        context.Request.Headers.Remove(OperationLineageHeaders.ProposalId);

        if (!StringValues.IsNullOrEmpty(token) && store.TryConsume(token, out var lineage))
        {
            context.Features.Set(lineage.AgentRequested ? AttestedOperationLineage.ForAgent : AttestedOperationLineage.Instance);
            Add(context, OperationLineageHeaders.OperationInstanceId, lineage.OperationInstanceId);
            Add(context, OperationLineageHeaders.AuditId, lineage.AuditId);
            Add(context, OperationLineageHeaders.ProposalId, lineage.ProposalId);
            if (lineage.Admission is { } admission && admission.Matches(context))
            {
                context.Features.Set(admission);
            }

            if (lineage.AgentOrigin)
            {
                Honua.Infrastructure.Authentication.AgentPrincipal.MarkAgent(context.User);
            }
        }

        await next(context).ConfigureAwait(false);
    }

    private static void Add(HttpContext context, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            context.Request.Headers[name] = value;
        }
    }
}

internal static class OperationLineageAttestationApplicationBuilderExtensions
{
    internal static IApplicationBuilder UseOperationLineageAttestation(this IApplicationBuilder app)
        => app.UseMiddleware<OperationLineageAttestationMiddleware>();
}
