// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Features.Admin;

public sealed record SecureConnectionAuditDetails
{
    public string? Name { get; init; }

    public string? Host { get; init; }

    public int? Port { get; init; }

    public string? Provider { get; init; }

    public bool? UsesSecretReference { get; init; }

    public bool? Healthy { get; init; }

    public string? Reason { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SecureConnectionAuditDetails))]
internal sealed partial class SecureConnectionAuditJsonContext : JsonSerializerContext
{
}

public sealed class SecureConnectionGovernance
{
    private readonly IConnectionHostAllowlist _hostAllowlist;
    private readonly IAuditLog _auditLog;
    private readonly IRequestSecretReferenceResolver _secretReferences;

    public SecureConnectionGovernance(
        IConnectionHostAllowlist hostAllowlist,
        IAuditLog auditLog,
        IRequestSecretReferenceResolver secretReferences)
    {
        _hostAllowlist = hostAllowlist ?? throw new ArgumentNullException(nameof(hostAllowlist));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _secretReferences = secretReferences ?? throw new ArgumentNullException(nameof(secretReferences));
    }

    public RequestSecretReferenceDecision EvaluateSecretReference(string? secretReference)
        => _secretReferences.Evaluate(secretReference);

    public Task<string> ResolveSecretReferenceAsync(string secretReference, CancellationToken cancellationToken)
        => _secretReferences.ResolveAsync(secretReference, cancellationToken);

    public bool IsEnforced => _hostAllowlist.IsEnforced;

    public bool IsHostEvaluable()
        => _hostAllowlist.IsEnforced;

    public Task<ConnectionHostDecision> EvaluateHostAsync(string? host, CancellationToken cancellationToken)
        => _hostAllowlist.EvaluateAsync(host, cancellationToken);

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
