// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Governance details captured for a secure-connection audit event. Pre-sanitized:
/// carries only non-sensitive connection metadata (never passwords or resolved secrets).
/// </summary>
public sealed record SecureConnectionAuditDetails
{
    /// <summary>Display name of the connection.</summary>
    public string? Name { get; init; }

    /// <summary>Connection destination host (display metadata only).</summary>
    public string? Host { get; init; }

    /// <summary>Connection destination port.</summary>
    public int? Port { get; init; }

    /// <summary>Provider engine for the connection.</summary>
    public string? Provider { get; init; }

    /// <summary>Whether credentials are stored via an external secret reference.</summary>
    public bool? UsesSecretReference { get; init; }

    /// <summary>Whether a connection test reported the destination healthy.</summary>
    public bool? Healthy { get; init; }

    /// <summary>Client-safe reason a governance check denied the action, when applicable.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// AOT-safe JSON context for serializing <see cref="SecureConnectionAuditDetails"/>.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SecureConnectionAuditDetails))]
internal sealed partial class SecureConnectionAuditJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Cross-cutting governance for secure-connection admin actions: enforces the outbound
/// connection host policy (#354, generalizing the SSRF guard #2004) and emits the
/// connection audit trail through the shared <see cref="IAuditLog"/> sink. Bundling both
/// concerns keeps individual endpoint handlers within their dependency budget.
/// </summary>
public sealed class SecureConnectionGovernance
{
    private readonly IConnectionHostAllowlist _hostAllowlist;
    private readonly IAuditLog _auditLog;
    private readonly IRequestSecretReferenceResolver _secretReferences;

    /// <summary>
    /// Creates the governance helper.
    /// </summary>
    /// <param name="hostAllowlist">Outbound connection host policy.</param>
    /// <param name="auditLog">Append-only audit sink.</param>
    /// <param name="secretReferences">Operator policy and resolver for request-supplied secret references.</param>
    public SecureConnectionGovernance(
        IConnectionHostAllowlist hostAllowlist,
        IAuditLog auditLog,
        IRequestSecretReferenceResolver secretReferences)
    {
        _hostAllowlist = hostAllowlist ?? throw new ArgumentNullException(nameof(hostAllowlist));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _secretReferences = secretReferences ?? throw new ArgumentNullException(nameof(secretReferences));
    }

    /// <summary>
    /// Evaluates a request-supplied secret reference against the operator policy without
    /// resolving it. A literal connection string or a value with embedded placeholders is not a
    /// reference and is refused.
    /// </summary>
    public RequestSecretReferenceDecision EvaluateSecretReference(string? secretReference)
        => _secretReferences.Evaluate(secretReference);

    /// <summary>
    /// Resolves a request-supplied secret reference under the operator policy.
    /// </summary>
    /// <exception cref="RequestSecretReferenceException">The reference is refused or cannot be resolved.</exception>
    public Task<string> ResolveSecretReferenceAsync(string secretReference, CancellationToken cancellationToken)
        => _secretReferences.ResolveAsync(secretReference, cancellationToken);

    /// <summary>Whether the connection host policy is actively enforcing any restriction.</summary>
    public bool IsEnforced => _hostAllowlist.IsEnforced;

    /// <summary>
    /// Determines whether a host should be evaluated against the policy for the current
    /// request. While a host policy is enforced every connection declares its host, including
    /// one that uses a secret reference: the declared host is evaluated here and the runtime
    /// resolver requires the resolved connection string to agree with it.
    /// </summary>
    public bool IsHostEvaluable()
        => _hostAllowlist.IsEnforced;

    /// <summary>
    /// Evaluates whether <paramref name="host"/> is a permitted connection destination
    /// under the configured policy.
    /// </summary>
    public Task<ConnectionHostDecision> EvaluateHostAsync(string? host, CancellationToken cancellationToken)
        => _hostAllowlist.EvaluateAsync(host, cancellationToken);

    /// <summary>
    /// Records a secure-connection audit event. Best-effort and non-blocking with respect
    /// to the caller (the underlying sink swallows transient failures).
    /// </summary>
    /// <param name="context">The current HTTP context (actor / correlation source).</param>
    /// <param name="action">Dotted-lowercase action, e.g. <c>connection.create</c>.</param>
    /// <param name="outcome">Outcome of the action.</param>
    /// <param name="resourceId">Connection identifier, when known.</param>
    /// <param name="details">Pre-sanitized connection metadata.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task RecordAsync(
        HttpContext context,
        string action,
        AuditOutcome outcome,
        string? resourceId,
        SecureConnectionAuditDetails details,
        CancellationToken cancellationToken)
    {
        var (actor, actorType) = ResolveActor(context);

        var auditEvent = new AuditEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            // Secure-connection registration is a control-plane configuration mutation.
            EventType = AuditEventType.ConfigChange,
            Actor = actor,
            ActorType = actorType,
            ResourceType = "connection",
            ResourceId = resourceId,
            Action = action,
            Outcome = outcome,
            CorrelationId = context.TraceIdentifier,
            Details = JsonSerializer.Serialize(details, SecureConnectionAuditJsonContext.Default.SecureConnectionAuditDetails)
        };

        return _auditLog.RecordAsync(auditEvent, cancellationToken);
    }

    private static (string Actor, AuditActorType ActorType) ResolveActor(HttpContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.User?.Identity?.Name))
        {
            return (context.User!.Identity!.Name!, AuditActorType.UserId);
        }

        // Shared admin API-key auth carries no Name claim; attribute the action to the
        // authenticated key id (mirrors HttpContextExtensions.GetUserIdentity) so the
        // audit trail preserves the acting principal rather than a constant placeholder.
        var apiKeyId = context.User?.FindFirst("api_key_id")?.Value;
        return string.IsNullOrWhiteSpace(apiKeyId)
            ? ("admin", AuditActorType.System)
            : ($"api-key:{apiKeyId}", AuditActorType.ApiKey);
    }
}
