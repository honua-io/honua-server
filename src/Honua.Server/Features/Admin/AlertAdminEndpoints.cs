// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Server.Features.Admin.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for managing geofence zones and alert rules.
/// </summary>
internal static class AlertAdminEndpoints
{
    private static readonly WKBReader _wkbReader = new();
    private static readonly WKTWriter _wktWriter = new();
    private const int RecentTriggerLimit = 10;
    private const string ChannelStatusConfigured = "configured";
    private const string ChannelStatusUnconfigured = "unconfigured";
    private const string ChannelStatusDisabled = "disabled";
    private const string ChannelStatusUnauthorized = "unauthorized";
    private const string ChannelStatusRateLimited = "rate_limited";
    private const string ChannelStatusFailing = "failing";

    public static void MapAlertAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/alerts")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Alerts")
            .RequireAdminAuthorization();

        group.MapGet("/zones", HandleListZones)
            .WithDisplayName("List Alert Zones")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapGet("/zones/{zoneId:long}", HandleGetZone)
            .WithDisplayName("Get Alert Zone")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPost("/zones", HandleCreateZone)
            .WithDisplayName("Create Alert Zone")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapPut("/zones/{zoneId:long}", HandleUpdateZone)
            .WithDisplayName("Update Alert Zone")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));

        group.MapDelete("/zones/{zoneId:long}", HandleDeleteZone)
            .WithDisplayName("Delete Alert Zone")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Delete }));

        group.MapGet("/rules", HandleListRules)
            .WithDisplayName("List Alert Rules")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapGet("/rules/{ruleId:long}", HandleGetRule)
            .WithDisplayName("Get Alert Rule")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPost("/rules", HandleCreateRule)
            .WithDisplayName("Create Alert Rule")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapPost("/rules/test", HandleTestRule)
            .WithDisplayName("Test Alert Rule")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapPut("/rules/{ruleId:long}", HandleUpdateRule)
            .WithDisplayName("Update Alert Rule")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));

        group.MapPut("/rules/{ruleId:long}/enabled", HandleSetRuleEnabled)
            .WithDisplayName("Set Alert Rule Enabled")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));

        group.MapGet("/rules/{ruleId:long}/health", HandleGetRuleHealth)
            .WithDisplayName("Get Alert Rule Health")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapDelete("/rules/{ruleId:long}", HandleDeleteRule)
            .WithDisplayName("Delete Alert Rule")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Delete }));
    }

    private static async Task<IResult> HandleListZones(
        string? serviceId,
        [FromServices] IAlertAdminStore store,
        CancellationToken cancellationToken)
    {
        var zones = await store.ListZonesAsync(serviceId, cancellationToken).ConfigureAwait(false);
        var payload = zones.Select(MapZoneResponse).ToArray();
        return Results.Json(ApiResponse<AlertZoneResponse[]>.CreateSuccess(payload), AlertAdminJsonContext.Default.ApiResponseAlertZoneResponseArray);
    }

    private static async Task<IResult> HandleGetZone(
        long zoneId,
        [FromServices] IAlertAdminStore store,
        CancellationToken cancellationToken)
    {
        var zone = await store.GetZoneAsync(zoneId, cancellationToken).ConfigureAwait(false);
        if (zone is null)
        {
            return NotFound($"Zone '{zoneId}' not found.");
        }

        return Results.Json(ApiResponse<AlertZoneResponse>.CreateSuccess(MapZoneResponse(zone)), AlertAdminJsonContext.Default.ApiResponseAlertZoneResponse);
    }

    private static async Task<IResult> HandleCreateZone(
        AlertZoneRequest request,
        [FromServices] IAlertAdminStore store,
        [FromServices] IGeometryService geometryService,
        [FromServices] IAuditLog auditLog,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!TryCreateZoneDefinition(0, request, geometryService, out var zone, out var error))
        {
            return BadRequest(error);
        }

        var created = await store.CreateZoneAsync(zone, cancellationToken).ConfigureAwait(false);
        await RecordZoneAuditAsync(auditLog, context, created, "alert_zone.create", created.IsActive, cancellationToken).ConfigureAwait(false);

        return Results.Json(ApiResponse<AlertZoneResponse>.CreateSuccess(MapZoneResponse(created)), AlertAdminJsonContext.Default.ApiResponseAlertZoneResponse);
    }

    private static async Task<IResult> HandleUpdateZone(
        long zoneId,
        AlertZoneRequest request,
        [FromServices] IAlertAdminStore store,
        [FromServices] IGeometryService geometryService,
        [FromServices] IAuditLog auditLog,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!TryCreateZoneDefinition(zoneId, request, geometryService, out var zone, out var error))
        {
            return BadRequest(error);
        }

        var updated = await store.UpdateZoneAsync(zone, cancellationToken).ConfigureAwait(false);
        if (updated is null)
        {
            return NotFound($"Zone '{zoneId}' not found.");
        }

        await RecordZoneAuditAsync(auditLog, context, updated, "alert_zone.update", updated.IsActive, cancellationToken).ConfigureAwait(false);

        return Results.Json(ApiResponse<AlertZoneResponse>.CreateSuccess(MapZoneResponse(updated)), AlertAdminJsonContext.Default.ApiResponseAlertZoneResponse);
    }

    private static async Task<IResult> HandleDeleteZone(
        long zoneId,
        [FromServices] IAlertAdminStore store,
        [FromServices] IAuditLog auditLog,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var deleted = await store.DeleteZoneAsync(zoneId, cancellationToken).ConfigureAwait(false);
        if (!deleted)
        {
            return NotFound($"Zone '{zoneId}' not found.");
        }

        await RecordAuditAsync(
            auditLog,
            context,
            resourceType: "alert_zone",
            resourceId: zoneId.ToString(CultureInfo.InvariantCulture),
            action: "alert_zone.delete",
            new AlertAdminAuditDetails { ZoneId = zoneId },
            cancellationToken).ConfigureAwait(false);

        return Results.Json(ApiResponse<object>.SuccessWithMessage("Zone deleted."), AlertAdminJsonContext.Default.ApiResponseObject);
    }

    private static async Task<IResult> HandleListRules(
        string? serviceId,
        int? layerId,
        [FromServices] IAlertAdminStore store,
        CancellationToken cancellationToken)
    {
        var rules = await store.ListRulesAsync(serviceId, layerId, cancellationToken).ConfigureAwait(false);
        var payload = rules.Select(MapRuleResponse).ToArray();
        return Results.Json(ApiResponse<AlertRuleResponse[]>.CreateSuccess(payload), AlertAdminJsonContext.Default.ApiResponseAlertRuleResponseArray);
    }

    private static async Task<IResult> HandleGetRule(
        long ruleId,
        [FromServices] IAlertAdminStore store,
        CancellationToken cancellationToken)
    {
        var rule = await store.GetRuleAsync(ruleId, cancellationToken).ConfigureAwait(false);
        if (rule is null)
        {
            return NotFound($"Rule '{ruleId}' not found.");
        }

        return Results.Json(ApiResponse<AlertRuleResponse>.CreateSuccess(MapRuleResponse(rule)), AlertAdminJsonContext.Default.ApiResponseAlertRuleResponse);
    }

    private static async Task<IResult> HandleCreateRule(
        AlertRuleRequest request,
        [FromServices] IAlertAdminStore store,
        [FromServices] IAlertEditionPolicy editionPolicy,
        [FromServices] IAuditLog auditLog,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!TryCreateRuleDefinition(0, request, out var rule, out var error))
        {
            return BadRequest(error);
        }

        var validation = await ValidateRuleDraftAsync(
            rule,
            draftZone: null,
            store,
            geometryService: null,
            editionPolicy,
            cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return BadRequest(validation.Errors[0]);
        }

        var created = await store.CreateRuleAsync(rule, cancellationToken).ConfigureAwait(false);
        await RecordRuleAuditAsync(auditLog, context, created, "alert_rule.create", created.IsActive, cancellationToken).ConfigureAwait(false);

        return Results.Json(ApiResponse<AlertRuleResponse>.CreateSuccess(MapRuleResponse(created)), AlertAdminJsonContext.Default.ApiResponseAlertRuleResponse);
    }

    private static async Task<IResult> HandleTestRule(
        AlertRuleTestRequest request,
        [FromServices] IAlertAdminStore store,
        [FromServices] IGeometryService geometryService,
        [FromServices] IAlertEditionPolicy editionPolicy,
        CancellationToken cancellationToken)
    {
        if (!TryCreateRuleDefinition(0, request.Rule, out var rule, out var error))
        {
            var failed = new AlertRuleTestResponse
            {
                IsValid = false,
                Errors = [error],
                Warnings = [],
                DeliveryChannels = [],
                EvaluatedAt = DateTimeOffset.UtcNow
            };

            return Results.Json(ApiResponse<AlertRuleTestResponse>.CreateSuccess(failed), AlertAdminJsonContext.Default.ApiResponseAlertRuleTestResponse);
        }

        var validation = await ValidateRuleDraftAsync(
            rule,
            request.Zone,
            store,
            geometryService,
            editionPolicy,
            cancellationToken).ConfigureAwait(false);

        var response = new AlertRuleTestResponse
        {
            IsValid = validation.IsValid,
            Errors = validation.Errors,
            Warnings = validation.Warnings,
            DeliveryChannels = validation.DeliveryChannels,
            EvaluatedAt = DateTimeOffset.UtcNow
        };

        return Results.Json(ApiResponse<AlertRuleTestResponse>.CreateSuccess(response), AlertAdminJsonContext.Default.ApiResponseAlertRuleTestResponse);
    }

    private static async Task<IResult> HandleUpdateRule(
        long ruleId,
        AlertRuleRequest request,
        [FromServices] IAlertAdminStore store,
        [FromServices] IAlertEditionPolicy editionPolicy,
        [FromServices] IAuditLog auditLog,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!TryCreateRuleDefinition(ruleId, request, out var rule, out var error))
        {
            return BadRequest(error);
        }

        var validation = await ValidateRuleDraftAsync(
            rule,
            draftZone: null,
            store,
            geometryService: null,
            editionPolicy,
            cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return BadRequest(validation.Errors[0]);
        }

        var updated = await store.UpdateRuleAsync(rule, cancellationToken).ConfigureAwait(false);
        if (updated is null)
        {
            return NotFound($"Rule '{ruleId}' not found.");
        }

        await RecordRuleAuditAsync(auditLog, context, updated, "alert_rule.update", updated.IsActive, cancellationToken).ConfigureAwait(false);

        return Results.Json(ApiResponse<AlertRuleResponse>.CreateSuccess(MapRuleResponse(updated)), AlertAdminJsonContext.Default.ApiResponseAlertRuleResponse);
    }

    private static async Task<IResult> HandleSetRuleEnabled(
        long ruleId,
        AlertRuleEnabledRequest request,
        [FromServices] IAlertAdminStore store,
        [FromServices] IAlertEditionPolicy editionPolicy,
        [FromServices] IAuditLog auditLog,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var existing = await store.GetRuleAsync(ruleId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return NotFound($"Rule '{ruleId}' not found.");
        }

        var requested = existing with { IsActive = request.Enabled };
        if (request.Enabled)
        {
            var validation = await ValidateRuleDraftAsync(
                requested,
                draftZone: null,
                store,
                geometryService: null,
                editionPolicy,
                cancellationToken).ConfigureAwait(false);
            if (!validation.IsValid)
            {
                return BadRequest(validation.Errors[0]);
            }
        }

        var updated = await store.UpdateRuleAsync(requested, cancellationToken).ConfigureAwait(false);
        if (updated is null)
        {
            return NotFound($"Rule '{ruleId}' not found.");
        }

        await RecordRuleAuditAsync(
            auditLog,
            context,
            updated,
            request.Enabled ? "alert_rule.enable" : "alert_rule.disable",
            request.Enabled,
            cancellationToken).ConfigureAwait(false);

        return Results.Json(ApiResponse<AlertRuleResponse>.CreateSuccess(MapRuleResponse(updated)), AlertAdminJsonContext.Default.ApiResponseAlertRuleResponse);
    }

    private static async Task<IResult> HandleGetRuleHealth(
        long ruleId,
        [FromServices] IAlertAdminStore store,
        [FromServices] IAlertEventQuery eventQuery,
        [FromServices] IAlertEditionPolicy editionPolicy,
        CancellationToken cancellationToken)
    {
        var rule = await store.GetRuleAsync(ruleId, cancellationToken).ConfigureAwait(false);
        if (rule is null)
        {
            return NotFound($"Rule '{ruleId}' not found.");
        }

        var health = await store.GetRuleHealthAsync(ruleId, RecentTriggerLimit, cancellationToken).ConfigureAwait(false);
        if (health is null)
        {
            return NotFound($"Rule '{ruleId}' not found.");
        }

        var triggers = await eventQuery.ListAsync(new AlertEventFilter
        {
            RuleId = ruleId,
            PageSize = RecentTriggerLimit
        }, cancellationToken).ConfigureAwait(false);

        return Results.Json(
            ApiResponse<AlertRuleHealthResponse>.CreateSuccess(MapRuleHealthResponse(rule, health, triggers.Items, editionPolicy)),
            AlertAdminJsonContext.Default.ApiResponseAlertRuleHealthResponse);
    }

    private static async Task<IResult> HandleDeleteRule(
        long ruleId,
        [FromServices] IAlertAdminStore store,
        [FromServices] IAuditLog auditLog,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var existing = await store.GetRuleAsync(ruleId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return NotFound($"Rule '{ruleId}' not found.");
        }

        var deleted = await store.DeleteRuleAsync(ruleId, cancellationToken).ConfigureAwait(false);
        if (!deleted)
        {
            return NotFound($"Rule '{ruleId}' not found.");
        }

        await RecordRuleAuditAsync(auditLog, context, existing, "alert_rule.delete", existing.IsActive, cancellationToken).ConfigureAwait(false);

        return Results.Json(ApiResponse<object>.SuccessWithMessage("Rule deleted."), AlertAdminJsonContext.Default.ApiResponseObject);
    }

    private static bool TryCreateZoneDefinition(
        long zoneId,
        AlertZoneRequest request,
        IGeometryService geometryService,
        out AlertZoneDefinition zone,
        out string error)
    {
        zone = default!;

        if (string.IsNullOrWhiteSpace(request.ServiceId))
        {
            error = "ServiceId is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.ZoneName))
        {
            error = "ZoneName is required.";
            return false;
        }

        if (!TryParseWkt(request.Wkt, request.Srid ?? 4326, geometryService, out var geometry, out error))
        {
            return false;
        }

        zone = new AlertZoneDefinition
        {
            ZoneId = zoneId,
            ServiceId = request.ServiceId.Trim(),
            ZoneName = request.ZoneName.Trim(),
            Geometry = geometry,
            GeometrySrid = request.Srid ?? 4326,
            Metadata = (request.Metadata ?? new Dictionary<string, string?>()).ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
            IsActive = request.IsActive
        };

        error = string.Empty;
        return true;
    }

    private static bool TryCreateRuleDefinition(long ruleId, AlertRuleRequest request, out AlertRuleDefinition rule, out string error)
    {
        rule = default!;

        if (string.IsNullOrWhiteSpace(request.ServiceId))
        {
            error = "ServiceId is required.";
            return false;
        }

        if (request.LayerId <= 0)
        {
            error = "LayerId must be positive.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.RuleName))
        {
            error = "RuleName is required.";
            return false;
        }

        if (!TryParseTriggerType(request.TriggerType, out var triggerType))
        {
            error = "TriggerType must be one of: enter, exit, dwell, threshold.";
            return false;
        }

        if (!TryParseSeverity(request.Severity, out var severity))
        {
            error = "Severity must be one of: info, warning, critical.";
            return false;
        }

        if (!TryParseEdition(request.EditionRequired, out var edition))
        {
            error = "EditionRequired must be one of: pro, enterprise.";
            return false;
        }

        if (!TryParseChannels(request.Channels ?? Array.Empty<string>(), out var channels, out error))
        {
            return false;
        }

        if (request.CooldownSeconds < 0)
        {
            error = "CooldownSeconds must be zero or greater.";
            return false;
        }

        var conditionsJson = string.IsNullOrWhiteSpace(request.ConditionsJson) ? "{}" : request.ConditionsJson;
        if (!TryValidateConditions(triggerType, conditionsJson, out error))
        {
            return false;
        }

        rule = new AlertRuleDefinition
        {
            RuleId = ruleId,
            ServiceId = request.ServiceId.Trim(),
            LayerId = request.LayerId,
            ZoneId = request.ZoneId,
            RuleName = request.RuleName.Trim(),
            TriggerType = triggerType,
            ConditionsJson = conditionsJson,
            CooldownSeconds = request.CooldownSeconds,
            Severity = severity,
            EditionRequired = edition,
            Channels = channels,
            IsActive = request.IsActive
        };

        error = string.Empty;
        return true;
    }

    private static async Task<RuleValidationResult> ValidateRuleDraftAsync(
        AlertRuleDefinition rule,
        AlertZoneRequest? draftZone,
        IAlertAdminStore store,
        IGeometryService? geometryService,
        IAlertEditionPolicy editionPolicy,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (!editionPolicy.IsRuleAllowed(rule))
        {
            errors.Add("The configured edition does not allow this rule trigger or tier requirement.");
        }

        var channelValidation = BuildChannelValidation(rule, editionPolicy);
        foreach (var channel in channelValidation)
        {
            if (channel.Status is ChannelStatusUnauthorized or ChannelStatusUnconfigured)
            {
                errors.Add(channel.Message);
            }

            if (channel.Status == ChannelStatusDisabled)
            {
                warnings.Add(channel.Message);
            }
        }

        await ValidateZoneReferenceAsync(rule, draftZone, store, geometryService, errors, warnings, cancellationToken)
            .ConfigureAwait(false);

        return new RuleValidationResult(
            errors.Count == 0,
            errors.ToArray(),
            warnings.ToArray(),
            channelValidation);
    }

    private static async Task ValidateZoneReferenceAsync(
        AlertRuleDefinition rule,
        AlertZoneRequest? draftZone,
        IAlertAdminStore store,
        IGeometryService? geometryService,
        List<string> errors,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (!RequiresZone(rule.TriggerType))
        {
            if (rule.ZoneId.HasValue)
            {
                errors.Add("ZoneId is only supported for enter, exit, and dwell alert rules.");
            }

            if (draftZone is not null)
            {
                errors.Add("Draft zones are only supported for enter, exit, and dwell alert rules.");
            }

            return;
        }

        if (draftZone is not null)
        {
            if (geometryService is null)
            {
                errors.Add("A geometry service is required to validate a draft zone.");
                return;
            }

            if (!TryCreateZoneDefinition(0, draftZone, geometryService, out var zone, out var error))
            {
                errors.Add(error);
                return;
            }

            if (!string.Equals(zone.ServiceId, rule.ServiceId, StringComparison.Ordinal))
            {
                errors.Add("Draft zone ServiceId must match the alert rule ServiceId.");
            }

            if (!zone.IsActive)
            {
                warnings.Add("Draft zone is inactive; the rule will not evaluate spatial transitions until the zone is active.");
            }

            if (rule.ZoneId.HasValue)
            {
                warnings.Add("Both ZoneId and a draft zone were provided; validation used the draft zone geometry.");
            }

            return;
        }

        if (!rule.ZoneId.HasValue)
        {
            errors.Add("ZoneId is required for enter, exit, and dwell alert rules.");
            return;
        }

        var existingZone = await store.GetZoneAsync(rule.ZoneId.Value, cancellationToken).ConfigureAwait(false);
        if (existingZone is null)
        {
            errors.Add($"Zone '{rule.ZoneId.Value.ToString(CultureInfo.InvariantCulture)}' was not found.");
            return;
        }

        if (!string.Equals(existingZone.ServiceId, rule.ServiceId, StringComparison.Ordinal))
        {
            errors.Add("Zone ServiceId must match the alert rule ServiceId.");
        }

        if (!existingZone.IsActive)
        {
            warnings.Add("The referenced zone is inactive; the rule will not evaluate spatial transitions until the zone is active.");
        }
    }

    private static bool RequiresZone(AlertTriggerType triggerType)
    {
        return triggerType is AlertTriggerType.Enter or AlertTriggerType.Exit or AlertTriggerType.Dwell;
    }

    private static AlertChannelValidationResponse[] BuildChannelValidation(
        AlertRuleDefinition rule,
        IAlertEditionPolicy editionPolicy)
    {
        if (rule.Channels.IsDefaultOrEmpty)
        {
            return [];
        }

        var results = new List<AlertChannelValidationResponse>(rule.Channels.Length);
        foreach (var channel in rule.Channels)
        {
            var isAllowed = editionPolicy.IsChannelAllowed(channel);
            var isConfigured = isAllowed && editionPolicy.IsChannelConfigured(channel);
            string status;
            string message;

            if (!isAllowed)
            {
                status = ChannelStatusUnauthorized;
                message = $"The configured edition does not allow the '{channel.ToExternalName()}' delivery channel.";
            }
            else if (!isConfigured)
            {
                status = ChannelStatusUnconfigured;
                message = $"The server is not configured to deliver the '{channel.ToExternalName()}' channel.";
            }
            else if (!rule.IsActive)
            {
                status = ChannelStatusDisabled;
                message = $"The '{channel.ToExternalName()}' channel is configured but the rule is disabled.";
            }
            else
            {
                status = ChannelStatusConfigured;
                message = $"The '{channel.ToExternalName()}' channel is available.";
            }

            results.Add(new AlertChannelValidationResponse
            {
                Channel = channel.ToExternalName(),
                Status = status,
                IsAllowed = isAllowed,
                IsConfigured = isConfigured,
                Message = message
            });
        }

        return results.ToArray();
    }

    private static bool TryValidateConditions(AlertTriggerType triggerType, string conditionsJson, out string error)
    {
        try
        {
            using var document = JsonDocument.Parse(conditionsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "ConditionsJson must be a JSON object.";
                return false;
            }

            if (triggerType == AlertTriggerType.Dwell &&
                !TryGetPositiveInt(root, "dwellSeconds", out _))
            {
                error = "Dwell rules require a positive dwellSeconds condition.";
                return false;
            }

            if (triggerType == AlertTriggerType.Threshold &&
                !TryValidateThresholdConditions(root, out error))
            {
                return false;
            }

            error = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            error = "ConditionsJson must be valid JSON.";
            return false;
        }
    }

    private static bool TryValidateThresholdConditions(JsonElement root, out string error)
    {
        if (!root.TryGetProperty("field", out var fieldProperty) ||
            fieldProperty.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(fieldProperty.GetString()))
        {
            error = "Threshold rules require a non-empty field condition.";
            return false;
        }

        if (!root.TryGetProperty("operator", out var operatorProperty) ||
            operatorProperty.ValueKind != JsonValueKind.String ||
            !IsSupportedThresholdOperator(operatorProperty.GetString()))
        {
            error = "Threshold rules require operator to be one of: >, >=, <, <=, ==, !=.";
            return false;
        }

        if (!root.TryGetProperty("value", out var valueProperty) ||
            !TryReadDouble(valueProperty, out _))
        {
            error = "Threshold rules require a numeric value condition.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryGetPositiveInt(JsonElement root, string propertyName, out int value)
    {
        value = 0;
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value))
        {
            return value > 0;
        }

        if (property.ValueKind == JsonValueKind.String &&
            int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return value > 0;
        }

        return false;
    }

    private static bool TryReadDouble(JsonElement value, out double parsed)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out parsed))
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
        }

        parsed = 0;
        return false;
    }

    private static bool IsSupportedThresholdOperator(string? value)
    {
        return value is ">" or ">=" or "<" or "<=" or "==" or "!=";
    }

    private static AlertZoneResponse MapZoneResponse(AlertZoneDefinition zone)
    {
        return new AlertZoneResponse
        {
            ZoneId = zone.ZoneId,
            ServiceId = zone.ServiceId,
            ZoneName = zone.ZoneName,
            Wkt = ToWkt(zone.Geometry),
            Srid = zone.GeometrySrid,
            Metadata = zone.Metadata.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            IsActive = zone.IsActive
        };
    }

    private static AlertRuleResponse MapRuleResponse(AlertRuleDefinition rule)
    {
        return new AlertRuleResponse
        {
            RuleId = rule.RuleId,
            ServiceId = rule.ServiceId,
            LayerId = rule.LayerId,
            ZoneId = rule.ZoneId,
            RuleName = rule.RuleName,
            TriggerType = rule.TriggerType.ToString().ToLowerInvariant(),
            ConditionsJson = rule.ConditionsJson,
            CooldownSeconds = rule.CooldownSeconds,
            Severity = rule.Severity.ToString().ToLowerInvariant(),
            EditionRequired = rule.EditionRequired.ToString().ToLowerInvariant(),
            Channels = rule.Channels.Select(static channel => channel.ToExternalName()).ToArray(),
            IsActive = rule.IsActive
        };
    }

    private static AlertRuleHealthResponse MapRuleHealthResponse(
        AlertRuleDefinition rule,
        AlertRuleHealthSnapshot health,
        IReadOnlyList<AlertEventSummary> recentTriggers,
        IAlertEditionPolicy editionPolicy)
    {
        return new AlertRuleHealthResponse
        {
            RuleId = health.RuleId,
            LastEvaluatedAt = health.LastEvaluatedAt,
            LastTriggeredAt = health.LastTriggeredAt,
            ActiveIncidentCount = health.ActiveIncidentCount,
            RecentTriggerCount = health.RecentTriggerCount,
            CoolingDownFeatureCount = health.CoolingDownFeatureCount,
            NextCooldownExpiresAt = health.NextCooldownExpiresAt,
            DeliveryFailureCount = health.DeliveryFailureCount,
            DeadLetterCount = health.DeadLetterCount,
            LinkedEventIds = health.LinkedEventIds.ToArray(),
            DeliveryChannels = MapDeliveryHealth(rule, health.DeliveryChannels, editionPolicy),
            RecentTriggers = recentTriggers.Select(MapRecentTrigger).ToArray()
        };
    }

    private static AlertRuleDeliveryHealthResponse[] MapDeliveryHealth(
        AlertRuleDefinition rule,
        ImmutableArray<AlertRuleDeliveryHealth> deliveryHealth,
        IAlertEditionPolicy editionPolicy)
    {
        var byChannel = deliveryHealth.ToDictionary(static health => health.ChannelType);
        var validationByChannel = BuildChannelValidation(rule, editionPolicy)
            .ToDictionary(
                static validation => validation.Channel,
                static validation => validation.Status,
                StringComparer.Ordinal);
        var channels = rule.Channels.IsDefaultOrEmpty
            ? deliveryHealth.Select(static health => health.ChannelType).ToArray()
            : rule.Channels.Concat(deliveryHealth.Select(static health => health.ChannelType)).Distinct().ToArray();

        return channels
            .Select(channel => byChannel.TryGetValue(channel, out var health)
                ? MapDeliveryHealth(rule, health, validationByChannel.GetValueOrDefault(channel.ToExternalName()))
                : MapDeliveryHealth(rule, new AlertRuleDeliveryHealth
                {
                    ChannelType = channel,
                    PendingCount = 0,
                    ProcessingCount = 0,
                    DeliveredCount = 0,
                    FailedCount = 0,
                    DeadLetterCount = 0
                }, validationByChannel.GetValueOrDefault(channel.ToExternalName())))
            .ToArray();
    }

    private static AlertRuleDeliveryHealthResponse MapDeliveryHealth(
        AlertRuleDefinition rule,
        AlertRuleDeliveryHealth health,
        string? validationStatus)
    {
        return new AlertRuleDeliveryHealthResponse
        {
            Channel = health.ChannelType.ToExternalName(),
            Status = ResolveDeliveryHealthStatus(rule, health, validationStatus),
            PendingCount = health.PendingCount,
            ProcessingCount = health.ProcessingCount,
            DeliveredCount = health.DeliveredCount,
            FailedCount = health.FailedCount,
            DeadLetterCount = health.DeadLetterCount,
            LastAttemptAt = health.LastAttemptAt,
            LastDeliveredAt = health.LastDeliveredAt,
            LastError = AlertDeliveryErrorSummaries.ToSanitizedSummary(health.LastError)
        };
    }

    private static AlertRuleRecentTriggerResponse MapRecentTrigger(AlertEventSummary summary)
    {
        return new AlertRuleRecentTriggerResponse
        {
            EventId = summary.EventId,
            TriggerType = summary.TriggerType.ToString().ToLowerInvariant(),
            Severity = summary.Severity.ToString().ToLowerInvariant(),
            OccurredAt = summary.OccurredAt,
            IncidentStatus = summary.IncidentStatus.ToString().ToLowerInvariant(),
            LifecycleStatus = summary.LifecycleStatus.ToString().ToLowerInvariant(),
            ResourceRef = "alert/" + summary.EventId.ToString(CultureInfo.InvariantCulture)
        };
    }

    private static string ResolveDeliveryHealthStatus(
        AlertRuleDefinition rule,
        AlertRuleDeliveryHealth health,
        string? validationStatus)
    {
        if (validationStatus is ChannelStatusUnauthorized or ChannelStatusUnconfigured or ChannelStatusDisabled)
        {
            return validationStatus;
        }

        if (!rule.IsActive)
        {
            return ChannelStatusDisabled;
        }

        if (health.DeadLetterCount > 0 || health.FailedCount > 0)
        {
            return AlertDeliveryErrorSummaries.IsRateLimited(health.LastError) ? ChannelStatusRateLimited : ChannelStatusFailing;
        }

        return ChannelStatusConfigured;
    }

    private static bool TryParseWkt(
        string? wkt,
        int srid,
        IGeometryService geometryService,
        out byte[]? wkb,
        out string error)
        => AlertZoneGeometryParser.TryParseWkt(wkt, srid, geometryService, out wkb, out error);

    private static string? ToWkt(byte[]? wkb)
    {
        if (wkb is null)
        {
            return null;
        }

        try
        {
            var geometry = _wkbReader.Read(wkb);
            return _wktWriter.Write(geometry);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (ParseException)
        {
            return null;
        }
    }

    private static bool TryParseTriggerType(string value, out AlertTriggerType triggerType)
    {
        triggerType = default;
        return !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) &&
               Enum.TryParse(value, true, out triggerType) &&
               Enum.IsDefined(triggerType);
    }

    private static bool TryParseSeverity(string value, out AlertSeverity severity)
    {
        severity = default;
        return !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) &&
               Enum.TryParse(value, true, out severity) &&
               Enum.IsDefined(severity);
    }

    private static bool TryParseEdition(string value, out AlertEdition edition)
    {
        edition = default;
        return !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) &&
               Enum.TryParse(value, true, out edition) &&
               Enum.IsDefined(edition);
    }

    private static bool TryParseChannels(
        string[] values,
        out ImmutableArray<AlertChannelType> channels,
        out string error)
    {
        if (values.Length == 0)
        {
            channels = ImmutableArray<AlertChannelType>.Empty;
            error = string.Empty;
            return true;
        }

        var builder = ImmutableArray.CreateBuilder<AlertChannelType>(values.Length);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (!AlertChannelNames.TryParse(value, out var channel))
            {
                channels = ImmutableArray<AlertChannelType>.Empty;
                error = $"Unsupported channel '{value}'.";
                return false;
            }

            builder.Add(channel);
        }

        channels = builder.Distinct().ToImmutableArray();
        error = string.Empty;
        return true;
    }

    private static Task RecordZoneAuditAsync(
        IAuditLog auditLog,
        HttpContext context,
        AlertZoneDefinition zone,
        string action,
        bool enabled,
        CancellationToken cancellationToken)
    {
        return RecordAuditAsync(
            auditLog,
            context,
            resourceType: "alert_zone",
            resourceId: zone.ZoneId.ToString(CultureInfo.InvariantCulture),
            action,
            new AlertAdminAuditDetails
            {
                ServiceId = zone.ServiceId,
                ZoneId = zone.ZoneId,
                Enabled = enabled
            },
            cancellationToken);
    }

    private static Task RecordRuleAuditAsync(
        IAuditLog auditLog,
        HttpContext context,
        AlertRuleDefinition rule,
        string action,
        bool enabled,
        CancellationToken cancellationToken)
    {
        return RecordAuditAsync(
            auditLog,
            context,
            resourceType: "alert_rule",
            resourceId: rule.RuleId.ToString(CultureInfo.InvariantCulture),
            action,
            new AlertAdminAuditDetails
            {
                ServiceId = rule.ServiceId,
                LayerId = rule.LayerId,
                RuleId = rule.RuleId,
                ZoneId = rule.ZoneId,
                Enabled = enabled
            },
            cancellationToken);
    }

    private static Task RecordAuditAsync(
        IAuditLog auditLog,
        HttpContext context,
        string resourceType,
        string? resourceId,
        string action,
        AlertAdminAuditDetails details,
        CancellationToken cancellationToken)
    {
        var actor = ResolveActor(context);
        var auditEvent = new AuditEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            EventType = AuditEventType.ConfigChange,
            Actor = actor,
            ActorType = string.Equals(actor, AuditEvent.AnonymousActor, StringComparison.Ordinal)
                ? AuditActorType.Anonymous
                : AuditActorType.UserId,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Action = action,
            Outcome = AuditOutcome.Success,
            CorrelationId = context.TraceIdentifier,
            Details = JsonSerializer.Serialize(details, AlertAdminJsonContext.Default.AlertAdminAuditDetails)
        };

        return auditLog.RecordAsync(auditEvent, cancellationToken);
    }

    private static string ResolveActor(HttpContext context)
    {
        return context.User?.Identity?.Name ?? AuditEvent.AnonymousActor;
    }

    private static IResult BadRequest(string message)
    {
        return Results.Json(
            ApiResponse<object>.Failure(message),
            AlertAdminJsonContext.Default.ApiResponseObject,
            statusCode: StatusCodes.Status400BadRequest);
    }

    private static IResult NotFound(string message)
    {
        return Results.Json(
            ApiResponse<object>.Failure(message),
            AlertAdminJsonContext.Default.ApiResponseObject,
            statusCode: StatusCodes.Status404NotFound);
    }

    private sealed record RuleValidationResult(
        bool IsValid,
        string[] Errors,
        string[] Warnings,
        AlertChannelValidationResponse[] DeliveryChannels);
}
