// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Nodes;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.Middleware;

internal static class HttpAuditEventFactory
{
    // Caller-supplied detail values are copied and capped and never replace the factory's own status, method, or tenant fields.
    internal const string ExtraDetailsItemKey = "honua.audit.extra-details";

    private const int MaxExtraDetailKeyLength = 64;
    private const int MaxExtraDetailValueLength = 128;

    public const string HttpResourceType = "http";

    public const string AuthFailureAction = "auth.failure";

    public const string AuthDeniedAction = "auth.denied";

    public static bool IsSuccessStatus(int status) => status is >= 200 and < 300;

    public static bool IsAuthRejection(int status)
        => status is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden;

    public static AuditEvent CreateAuthOutcomeEvent(
        HttpContext context,
        int status,
        DateTimeOffset timestamp,
        string? code = null,
        bool includeLineage = false)
    {
        ArgumentNullException.ThrowIfNull(context);

        var forbidden = status == StatusCodes.Status403Forbidden;
        return new AuditEvent
        {
            Timestamp = timestamp,
            EventType = forbidden ? AuditEventType.Authorization : AuditEventType.Authentication,
            Actor = AuditContextResolver.ResolveActor(context, out var actorType),
            ActorType = actorType,
            ResourceType = HttpResourceType,
            ResourceId = context.Request.Path.HasValue ? context.Request.Path.Value : null,
            Action = forbidden ? AuthDeniedAction : AuthFailureAction,
            Outcome = forbidden ? AuditOutcome.Denied : AuditOutcome.Failure,
            CorrelationId = AuditContextResolver.ResolveCorrelationId(context),
            RemoteIp = AuditContextResolver.ResolveRemoteIp(context),
            UserAgent = AuditContextResolver.ResolveUserAgent(context),
            Details = BuildDetails(context, status, code, includeLineage),
        };
    }

    public static AuditEvent CreateDescriptorEvent(
        HttpContext context,
        AuditActionDescriptor descriptor,
        int status,
        DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(descriptor);

        var outcome = status switch
        {
            StatusCodes.Status403Forbidden => AuditOutcome.Denied,
            _ when IsSuccessStatus(status) => AuditOutcome.Success,
            _ => AuditOutcome.Failure,
        };

        return new AuditEvent
        {
            Timestamp = timestamp,
            EventType = descriptor.EventType,
            Actor = AuditContextResolver.ResolveActor(context, out var actorType),
            ActorType = actorType,
            ResourceType = descriptor.ResourceType,
            ResourceId = context.Request.Path.HasValue ? context.Request.Path.Value : null,
            Action = descriptor.Action,
            Outcome = outcome,
            CorrelationId = AuditContextResolver.ResolveCorrelationId(context),
            RemoteIp = AuditContextResolver.ResolveRemoteIp(context),
            UserAgent = AuditContextResolver.ResolveUserAgent(context),
            Details = BuildDetails(context, status, code: null, includeLineage: true),
        };
    }

    private static string BuildDetails(HttpContext context, int status, string? code, bool includeLineage)
    {
        var details = new JsonObject
        {
            ["status"] = status,
            ["method"] = context.Request.Method,
            ["tenantId"] = context.RequestServices?.GetService<ITenantContext>()?.TenantId,
        };
        if (!string.IsNullOrWhiteSpace(code))
        {
            details["code"] = code;
        }

        if (includeLineage)
        {
            AddHeader(details, context, "operationInstanceId", "X-Honua-Operation-Instance-Id");
            AddHeader(details, context, "acceptedAuditId", "X-Honua-Audit-Id");
            AddHeader(details, context, "proposalId", "X-Honua-Proposal-Id");
        }

        AddExtraDetails(details, context);
        return details.ToJsonString();
    }

    private static void AddExtraDetails(JsonObject details, HttpContext context)
    {
        if (!context.Items.TryGetValue(ExtraDetailsItemKey, out var raw)
            || raw is not IReadOnlyDictionary<string, string> extras)
        {
            return;
        }

        foreach (var pair in extras)
        {
            if (!IsExtraDetailKey(pair.Key) || details.ContainsKey(pair.Key))
            {
                continue;
            }

            var value = pair.Value ?? string.Empty;
            if (value.Length > MaxExtraDetailValueLength)
            {
                value = value[..MaxExtraDetailValueLength];
            }

            details[pair.Key] = value;
        }
    }

    private static bool IsExtraDetailKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxExtraDetailKeyLength || !char.IsAsciiLetter(key[0]))
        {
            return false;
        }

        for (var i = 1; i < key.Length; i++)
        {
            if (!char.IsAsciiLetterOrDigit(key[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static void AddHeader(JsonObject details, HttpContext context, string propertyName, string headerName)
    {
        if (context.Request.Headers.TryGetValue(headerName, out var values) && values.Count > 0)
        {
            details[propertyName] = values[0];
        }
    }
}
