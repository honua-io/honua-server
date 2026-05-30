// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.AuditLog.Abstractions;

namespace Honua.Infrastructure.Authentication.ClientCertificates;

internal static class ClientCertificateAudit
{
    private const string CorrelationIdHeader = "X-Correlation-ID";

    public static async Task RecordAuthenticationAsync(
        HttpContext context,
        ClientCertificateValidationResult result,
        string action,
        AuditOutcome outcome)
    {
        if (context.Items.ContainsKey(ClientCertificateHttpContextItems.AuditEmitted) &&
            action is "mtls.login.success" or "mtls.login.failure")
        {
            return;
        }

        context.Items[ClientCertificateHttpContextItems.AuditEmitted] = true;
        SetActivityTags(result);

        var auditLog = context.RequestServices.GetService<IAuditLog>();
        if (auditLog is null)
        {
            return;
        }

        var details = new ClientCertificateAuditDetails
        {
            ProfileId = result.ProfileId,
            EnvironmentId = result.EnvironmentId,
            ResultCode = result.ErrorCode.HasValue
                ? ClientCertificateErrorCodes.ToStableCode(result.ErrorCode.Value)
                : "success",
            CertificateFingerprintSha256 = result.FingerprintSha256,
            IssuerHash = result.IssuerHash,
            DaysUntilExpiry = result.DaysUntilExpiry,
            MappingId = result.MappingId,
        };

        var auditEvent = new AuditEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            EventType = AuditEventType.Authentication,
            Actor = ResolveActor(result.Principal, result.PrincipalId),
            ActorType = result.Succeeded ? AuditActorType.UserId : AuditActorType.Anonymous,
            ResourceType = "client_certificate",
            ResourceId = result.ProfileId,
            Action = action,
            Outcome = outcome,
            CorrelationId = ResolveCorrelationId(context),
            RemoteIp = context.Connection.RemoteIpAddress?.ToString(),
            UserAgent = context.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null,
            Details = JsonSerializer.Serialize(
                details,
                ClientCertificateInfrastructureJsonContext.Default.ClientCertificateAuditDetails),
        };

        await RecordBestEffortAsync(auditLog, auditEvent, context.RequestAborted).ConfigureAwait(false);
    }

    public static async Task RecordConfigChangeAsync(
        HttpContext context,
        string action,
        string? profileId,
        string? environmentId,
        string? mappingId = null,
        string? resultCode = "success")
    {
        var auditLog = context.RequestServices.GetService<IAuditLog>();
        if (auditLog is null)
        {
            return;
        }

        var details = new ClientCertificateAuditDetails
        {
            ProfileId = profileId,
            EnvironmentId = environmentId,
            ResultCode = resultCode,
            MappingId = mappingId,
        };

        var auditEvent = new AuditEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            EventType = AuditEventType.ConfigChange,
            Actor = ResolveActor(context.User, context.User?.FindFirstValue(ClaimTypes.NameIdentifier)),
            ActorType = context.User?.Identity?.IsAuthenticated == true ? AuditActorType.UserId : AuditActorType.Anonymous,
            ResourceType = "client_certificate_trust",
            ResourceId = profileId,
            Action = action,
            Outcome = AuditOutcome.Success,
            CorrelationId = ResolveCorrelationId(context),
            RemoteIp = context.Connection.RemoteIpAddress?.ToString(),
            UserAgent = context.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null,
            Details = JsonSerializer.Serialize(
                details,
                ClientCertificateInfrastructureJsonContext.Default.ClientCertificateAuditDetails),
        };

        await RecordBestEffortAsync(auditLog, auditEvent, context.RequestAborted).ConfigureAwait(false);
    }

    private static void SetActivityTags(ClientCertificateValidationResult result)
    {
        var activity = Activity.Current;
        if (activity is null)
        {
            return;
        }

        activity.SetTag("auth.scheme", ClientCertificateAuthenticationDefaults.AuthenticationScheme);
        activity.SetTag("mtls.profile_id", result.ProfileId);
        activity.SetTag("mtls.environment_id", result.EnvironmentId);
        activity.SetTag("mtls.result", result.Succeeded ? "success" : "failure");
        if (result.ErrorCode.HasValue)
        {
            activity.SetTag("mtls.error_code", ClientCertificateErrorCodes.ToStableCode(result.ErrorCode.Value));
        }
    }

    private static async Task RecordBestEffortAsync(
        IAuditLog auditLog,
        AuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            await auditLog.RecordAsync(auditEvent, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Audit sinks are best-effort and must not break authentication or admin writes.
        }
    }

    private static string ResolveActor(ClaimsPrincipal? principal, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            return fallback;
        }

        var identity = principal?.Identity;
        return identity is { IsAuthenticated: true } && !string.IsNullOrWhiteSpace(identity.Name)
            ? identity.Name
            : AuditEvent.AnonymousActor;
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        if (context.Response.Headers.TryGetValue(CorrelationIdHeader, out var headerValue) &&
            !string.IsNullOrWhiteSpace(headerValue.ToString()))
        {
            return headerValue.ToString();
        }

        return string.IsNullOrWhiteSpace(context.TraceIdentifier)
            ? Guid.NewGuid().ToString("D")
            : context.TraceIdentifier;
    }
}

internal sealed record ClientCertificateAuditDetails
{
    public string? ProfileId { get; init; }

    public string? EnvironmentId { get; init; }

    public string? ResultCode { get; init; }

    public string? CertificateFingerprintSha256 { get; init; }

    public string? IssuerHash { get; init; }

    public int? DaysUntilExpiry { get; init; }

    public string? MappingId { get; init; }
}
