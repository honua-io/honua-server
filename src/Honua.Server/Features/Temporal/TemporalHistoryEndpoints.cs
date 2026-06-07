// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Temporal.Abstractions;
using Honua.Core.Features.Temporal.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Temporal;

/// <summary>
/// Admin endpoint group for slice 1 of the temporal data history API (honua-server#1166):
/// capability discovery and as-of reads. These are thin adapters — they parse, authorize
/// (temporal-history read), delegate to <see cref="ITemporalHistoryService"/>, and format results.
/// </summary>
/// <remarks>
/// Diff, per-feature timeline, attribution, and governed rollback are deferred to later slices.
/// The group uses a distinct <see cref="AuthenticationExtensions.TemporalHistoryReadPolicy"/> so the
/// history-read surface is authorized separately from current reads and admin mutations.
/// </remarks>
internal static partial class TemporalHistoryEndpoints
{
    /// <summary>Log category for temporal history endpoints.</summary>
    internal sealed class TemporalHistoryEndpointsLog;

    /// <summary>Maps the temporal history admin endpoints.</summary>
    public static IEndpointRouteBuilder MapTemporalHistoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/api/v{version:apiVersion}/temporal/services/{serviceId}/layers/{layerId:int}")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Temporal")
            .WithDescription("Read-only temporal data history: capability discovery and as-of reads (slice 1 of #1166).")
            .RequireTemporalHistoryRead();

        _ = group.MapGet("/capabilities", HandleGetCapabilityAsync)
            .WithName("GetTemporalCapability")
            .WithSummary("Report whether a layer supports temporal history and which primitives back it.");

        _ = group.MapGet("/as-of", HandleReadAsOfAsync)
            .WithName("ReadTemporalAsOf")
            .WithSummary("Read a layer's collapsed feature changes as-of a generation cursor (timestamp cursors are a deferred #1166 follow-up).");

        return endpoints;
    }

    private static async Task<IResult> HandleGetCapabilityAsync(
        string serviceId,
        int layerId,
        [FromServices] ITemporalHistoryService service,
        HttpContext context)
    {
        try
        {
            var capability = await service.GetCapabilityAsync(serviceId, layerId, context.RequestAborted).ConfigureAwait(false);
            return Results.Json(ToResponse(capability), TemporalHistoryApiJsonContext.Default.TemporalCapabilityResponse);
        }
        catch (Exception ex) when (TemporalProblemMapping.TryMap(ex, context, out var problem))
        {
            return problem;
        }
        catch (Exception ex)
        {
            LogEndpointFailed(context, "temporal.capabilities", ex);
            return CreateGenericProblem(context);
        }
    }

    private static async Task<IResult> HandleReadAsOfAsync(
        string serviceId,
        int layerId,
        [FromQuery] long? generation,
        [FromQuery] string? timestamp,
        [FromQuery] int? limit,
        [FromServices] ITemporalHistoryService service,
        HttpContext context)
    {
        try
        {
            DateTimeOffset? parsedTimestamp = null;
            if (!string.IsNullOrWhiteSpace(timestamp))
            {
                if (!DateTimeOffset.TryParse(
                        timestamp,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var ts))
                {
                    return ProblemDetailsHelpers.CreateAdminProblem(
                        context,
                        StatusCodes.Status400BadRequest,
                        ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                        "Query parameter 'timestamp' must be a valid ISO-8601 date-time.");
                }

                parsedTimestamp = ts;
            }

            var request = new TemporalAsOfRequest(generation, parsedTimestamp, limit);
            var result = await service.ReadAsOfAsync(serviceId, layerId, request, context.RequestAborted).ConfigureAwait(false);
            return Results.Json(ToResponse(result), TemporalHistoryApiJsonContext.Default.TemporalAsOfResponse);
        }
        catch (Exception ex) when (TemporalProblemMapping.TryMap(ex, context, out var problem))
        {
            return problem;
        }
        catch (Exception ex)
        {
            LogEndpointFailed(context, "temporal.as-of", ex);
            return CreateGenericProblem(context);
        }
    }

    private static TemporalCapabilityResponse ToResponse(TemporalCapability capability)
        => new()
        {
            ServiceId = capability.ServiceId,
            LayerId = capability.LayerId,
            LayerName = capability.LayerName,
            SupportsHistory = capability.SupportsHistory,
            SupportsAsOf = capability.SupportsAsOf,
            TemporalColumn = capability.TemporalColumn,
            CursorKind = capability.CursorKind.ToString(),
            CurrentGeneration = capability.CurrentGeneration,
            Deferred = new TemporalDeferredCapabilitiesResponse
            {
                SupportsDiff = capability.DeferredCapabilities.SupportsDiff,
                SupportsTimeline = capability.DeferredCapabilities.SupportsTimeline,
                SupportsAttribution = capability.DeferredCapabilities.SupportsAttribution,
                SupportsRollback = capability.DeferredCapabilities.SupportsRollback
            }
        };

    private static TemporalAsOfResponse ToResponse(TemporalAsOfResult result)
        => new()
        {
            ServiceId = result.ServiceId,
            LayerId = result.LayerId,
            RequestedCursorKind = result.RequestedCursorKind.ToString(),
            ResolvedGeneration = result.ResolvedGeneration,
            CurrentGeneration = result.CurrentGeneration,
            Features = result.Features
                .Select(static feature => new TemporalFeatureStateResponse
                {
                    ObjectId = feature.ObjectId,
                    Operation = feature.NetOperation.ToString(),
                    ChangedAt = feature.ChangedAtUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                    Attributes = feature.Attributes
                })
                .ToArray()
        };

    private static IResult CreateGenericProblem(HttpContext context)
        => ProblemDetailsHelpers.CreateAdminProblem(
            context,
            StatusCodes.Status500InternalServerError,
            ProblemDetailsHelpers.GetTitle(StatusCodes.Status500InternalServerError),
            "An internal error occurred while processing the temporal history request.");

    private static void LogEndpointFailed(HttpContext context, string operation, Exception exception)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<TemporalHistoryEndpointsLog>>();
        Log.EndpointFailed(logger, operation, exception);
    }

    private static partial class Log
    {
        [LoggerMessage(12120, LogLevel.Error, "Temporal history endpoint {Operation} failed")]
        public static partial void EndpointFailed(
            ILogger logger,
            string operation,
            Exception exception);
    }
}
