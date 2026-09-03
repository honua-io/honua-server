// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

    internal static void Apply(
        HttpRequestMessage message,
        OperationPolicyContext context,
        OperationLineageAttestationStore attestationStore)
    {
        Add(message, OperationInstanceId, context.OperationInstanceId);
        Add(message, CorrelationId, context.CorrelationId);
        Add(message, AuditId, context.AuditId);
        Add(message, ProposalId, context.ProposalId ?? context.ApprovedProposalId);
        Add(message, Attestation, attestationStore.Issue(context));
    }

    private static void Add(HttpRequestMessage message, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }
    }
}

internal sealed class OperationLineageAttestationStore(TimeProvider clock)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Entry> _pending =
        new(StringComparer.Ordinal);

    internal string Issue(OperationPolicyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        _pending[token] = new Entry(
            context.OperationInstanceId,
            context.AuditId,
            context.ProposalId ?? context.ApprovedProposalId,
            clock.GetUtcNow().Add(Lifetime));
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

        value = new OperationLineageAttestation(
            entry.OperationInstanceId,
            entry.AuditId,
            entry.ProposalId);
        return true;
    }

    private sealed record Entry(
        string? OperationInstanceId,
        string? AuditId,
        string? ProposalId,
        DateTimeOffset ExpiresAt);
}

internal readonly record struct OperationLineageAttestation(
    string? OperationInstanceId,
    string? AuditId,
    string? ProposalId);

internal sealed class OperationLineageAttestationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, OperationLineageAttestationStore store)
    {
        var token = context.Request.Headers[OperationLineageHeaders.Attestation].ToString();

        context.Request.Headers.Remove(OperationLineageHeaders.Attestation);
        context.Request.Headers.Remove(OperationLineageHeaders.OperationInstanceId);
        context.Request.Headers.Remove(OperationLineageHeaders.AuditId);
        context.Request.Headers.Remove(OperationLineageHeaders.ProposalId);

        if (!StringValues.IsNullOrEmpty(token) && store.TryConsume(token, out var lineage))
        {
            Add(context, OperationLineageHeaders.OperationInstanceId, lineage.OperationInstanceId);
            Add(context, OperationLineageHeaders.AuditId, lineage.AuditId);
            Add(context, OperationLineageHeaders.ProposalId, lineage.ProposalId);
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
