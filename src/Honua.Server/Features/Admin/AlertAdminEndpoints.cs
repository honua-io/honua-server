// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for managing geofence zones and alert rules.
/// </summary>
internal static class AlertAdminEndpoints
{
    private static readonly WKTReader _wktReader = new();
    private static readonly WKBReader _wkbReader = new();
    private static readonly WKTWriter _wktWriter = new();
    private static readonly WKBWriter _wkbWriter = new();

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

        group.MapPost("/rules", HandleCreateRule)
            .WithDisplayName("Create Alert Rule")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapPut("/rules/{ruleId:long}", HandleUpdateRule)
            .WithDisplayName("Update Alert Rule")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));

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

    private static async Task<IResult> HandleCreateZone(
        AlertZoneRequest request,
        [FromServices] IAlertAdminStore store,
        CancellationToken cancellationToken)
    {
        if (!TryCreateZoneDefinition(0, request, out var zone, out var error))
        {
            return BadRequest(error);
        }

        var created = await store.CreateZoneAsync(zone, cancellationToken).ConfigureAwait(false);
        return Results.Json(ApiResponse<AlertZoneResponse>.CreateSuccess(MapZoneResponse(created)), AlertAdminJsonContext.Default.ApiResponseAlertZoneResponse);
    }

    private static async Task<IResult> HandleUpdateZone(
        long zoneId,
        AlertZoneRequest request,
        [FromServices] IAlertAdminStore store,
        CancellationToken cancellationToken)
    {
        if (!TryCreateZoneDefinition(zoneId, request, out var zone, out var error))
        {
            return BadRequest(error);
        }

        var updated = await store.UpdateZoneAsync(zone, cancellationToken).ConfigureAwait(false);
        if (updated is null)
        {
            return NotFound($"Zone '{zoneId}' not found.");
        }

        return Results.Json(ApiResponse<AlertZoneResponse>.CreateSuccess(MapZoneResponse(updated)), AlertAdminJsonContext.Default.ApiResponseAlertZoneResponse);
    }

    private static async Task<IResult> HandleDeleteZone(
        long zoneId,
        [FromServices] IAlertAdminStore store,
        CancellationToken cancellationToken)
    {
        var deleted = await store.DeleteZoneAsync(zoneId, cancellationToken).ConfigureAwait(false);
        if (!deleted)
        {
            return NotFound($"Zone '{zoneId}' not found.");
        }

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

    private static async Task<IResult> HandleCreateRule(
        AlertRuleRequest request,
        [FromServices] IAlertAdminStore store,
        [FromServices] IAlertEditionPolicy editionPolicy,
        CancellationToken cancellationToken)
    {
        if (!TryCreateRuleDefinition(0, request, out var rule, out var error))
        {
            return BadRequest(error);
        }

        if (!ValidateRuleExecution(editionPolicy, rule, out error))
        {
            return BadRequest(error);
        }

        var created = await store.CreateRuleAsync(rule, cancellationToken).ConfigureAwait(false);
        return Results.Json(ApiResponse<AlertRuleResponse>.CreateSuccess(MapRuleResponse(created)), AlertAdminJsonContext.Default.ApiResponseAlertRuleResponse);
    }

    private static async Task<IResult> HandleUpdateRule(
        long ruleId,
        AlertRuleRequest request,
        [FromServices] IAlertAdminStore store,
        [FromServices] IAlertEditionPolicy editionPolicy,
        CancellationToken cancellationToken)
    {
        if (!TryCreateRuleDefinition(ruleId, request, out var rule, out var error))
        {
            return BadRequest(error);
        }

        if (!ValidateRuleExecution(editionPolicy, rule, out error))
        {
            return BadRequest(error);
        }

        var updated = await store.UpdateRuleAsync(rule, cancellationToken).ConfigureAwait(false);
        if (updated is null)
        {
            return NotFound($"Rule '{ruleId}' not found.");
        }

        return Results.Json(ApiResponse<AlertRuleResponse>.CreateSuccess(MapRuleResponse(updated)), AlertAdminJsonContext.Default.ApiResponseAlertRuleResponse);
    }

    private static async Task<IResult> HandleDeleteRule(
        long ruleId,
        [FromServices] IAlertAdminStore store,
        CancellationToken cancellationToken)
    {
        var deleted = await store.DeleteRuleAsync(ruleId, cancellationToken).ConfigureAwait(false);
        if (!deleted)
        {
            return NotFound($"Rule '{ruleId}' not found.");
        }

        return Results.Json(ApiResponse<object>.SuccessWithMessage("Rule deleted."), AlertAdminJsonContext.Default.ApiResponseObject);
    }

    private static bool TryCreateZoneDefinition(long zoneId, AlertZoneRequest request, out AlertZoneDefinition zone, out string error)
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

        if (!TryParseWkt(request.Wkt, request.Srid ?? 4326, out var geometry, out error))
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

        rule = new AlertRuleDefinition
        {
            RuleId = ruleId,
            ServiceId = request.ServiceId.Trim(),
            LayerId = request.LayerId,
            ZoneId = request.ZoneId,
            RuleName = request.RuleName.Trim(),
            TriggerType = triggerType,
            ConditionsJson = string.IsNullOrWhiteSpace(request.ConditionsJson) ? "{}" : request.ConditionsJson,
            CooldownSeconds = Math.Max(0, request.CooldownSeconds),
            Severity = severity,
            EditionRequired = edition,
            Channels = channels,
            IsActive = request.IsActive
        };

        error = string.Empty;
        return true;
    }

    private static bool ValidateRuleExecution(IAlertEditionPolicy editionPolicy, AlertRuleDefinition rule, out string error)
    {
        if (!editionPolicy.IsRuleAllowed(rule))
        {
            error = "The configured edition does not allow this rule trigger or tier requirement.";
            return false;
        }

        foreach (var channel in rule.Channels)
        {
            if (!editionPolicy.IsChannelAllowed(channel))
            {
                error = $"The configured edition does not allow the '{channel}' delivery channel.";
                return false;
            }

            if (!editionPolicy.IsChannelConfigured(channel))
            {
                error = $"The server is not configured to deliver the '{channel}' channel.";
                return false;
            }
        }

        error = string.Empty;
        return true;
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

    private static bool TryParseWkt(string? wkt, int srid, out byte[]? wkb, out string error)
    {
        if (string.IsNullOrWhiteSpace(wkt))
        {
            wkb = null;
            error = string.Empty;
            return true;
        }

        try
        {
            Geometry geometry = _wktReader.Read(wkt);
            geometry.SRID = srid;

            if (geometry is Polygon polygon)
            {
                geometry = new MultiPolygon(new[] { polygon }) { SRID = srid };
            }

            if (geometry is not MultiPolygon)
            {
                wkb = null;
                error = "Zone geometry must be Polygon or MultiPolygon.";
                return false;
            }

            wkb = _wkbWriter.Write(geometry);
            error = string.Empty;
            return true;
        }
        catch (ParseException)
        {
            wkb = null;
            error = "Invalid WKT geometry.";
            return false;
        }
    }

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
        return Enum.TryParse(value, true, out triggerType);
    }

    private static bool TryParseSeverity(string value, out AlertSeverity severity)
    {
        return Enum.TryParse(value, true, out severity);
    }

    private static bool TryParseEdition(string value, out AlertEdition edition)
    {
        return Enum.TryParse(value, true, out edition);
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
}
