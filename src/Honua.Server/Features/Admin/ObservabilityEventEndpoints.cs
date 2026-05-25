// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Monitoring;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Console Operate unified event timeline + log buffer endpoints (#1168).
/// </summary>
internal static class ObservabilityEventEndpoints
{
    public static void MapObservabilityEventEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/observability")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Observability")
            .RequireAdminAuthorization();

        group.MapGet("/events", HandleListEvents)
            .WithDisplayName("List Operate Events")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapGet("/logs", HandleListLogs)
            .WithDisplayName("List Recent Server Logs")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
    }

    private static async Task<IResult> HandleListEvents(
        HttpRequest request,
        [FromServices] IOperateEventFeed feed,
        CancellationToken cancellationToken)
    {
        if (!TryParseEventFilter(request.Query, out var filter, out var error))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest, ProblemDetailsHelpers.GetTitle(400), error);
        }

        var page = await feed.ListAsync(filter, cancellationToken).ConfigureAwait(false);
        var response = new OperateEventPageResponse
        {
            Items = page.Items.Select(MapEvent).ToArray(),
            PartialResult = page.PartialResult,
            SourceErrors = page.SourceErrors?.ToDictionary(
                pair => pair.Key.ToString().ToLowerInvariant(),
                pair => pair.Value)
        };

        return Results.Json(response, ObservabilityJsonContext.Default.OperateEventPageResponse);
    }

    private static IResult HandleListLogs([FromServices] RecentErrorBuffer buffer)
    {
        var entries = buffer.Snapshot();
        var items = new OperateLogEntryResponse[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            items[i] = new OperateLogEntryResponse
            {
                Timestamp = entry.Timestamp,
                Level = entry.StatusCode >= StatusCodes.Status500InternalServerError ? "error" : "warning",
                Path = entry.Path,
                StatusCode = entry.StatusCode,
                CorrelationId = entry.CorrelationId,
                Message = entry.Message
            };
        }

        var response = new OperateLogPageResponse
        {
            InstanceId = buffer.InstanceId,
            Capacity = buffer.Capacity,
            Items = items
        };

        return Results.Json(response, ObservabilityJsonContext.Default.OperateLogPageResponse);
    }

    internal static bool TryParseEventFilter(IQueryCollection query, out OperateEventFilter filter, out string error)
    {
        filter = new OperateEventFilter();
        error = string.Empty;

        if (!QueryFilterParsers.TryParseDateTimeOffset(query, "from", out var from, out var parseError) ||
            !QueryFilterParsers.TryParseDateTimeOffset(query, "to", out var to, out parseError) ||
            !QueryFilterParsers.TryParseInt(query, "layerId", out var layerId, out parseError) ||
            !QueryFilterParsers.TryParseInt(query, "pageSize", out var pageSize, out parseError) ||
            !QueryFilterParsers.TryParseEnumList<OperateEventKind>(query, "kind", out var kinds, out parseError))
        {
            error = parseError;
            return false;
        }

        OperateEventSeverity? minSeverity = null;
        if (query.TryGetValue("minSeverity", out var rawSeverity) && !string.IsNullOrWhiteSpace(rawSeverity))
        {
            if (!QueryFilterParsers.TryParseDefinedEnum<OperateEventSeverity>(rawSeverity!, out var parsedSeverity))
            {
                error = $"'minSeverity' contains unsupported value '{rawSeverity}'.";
                return false;
            }

            minSeverity = parsedSeverity;
        }

        filter = filter with
        {
            From = from,
            To = to,
            ServiceId = QueryFilterParsers.GetString(query, "serviceId"),
            LayerId = layerId,
            Kinds = kinds,
            MinimumSeverity = minSeverity,
            CorrelationId = QueryFilterParsers.GetString(query, "correlationId"),
            TraceId = QueryFilterParsers.GetString(query, "traceId"),
            RequestId = QueryFilterParsers.GetString(query, "requestId"),
            OperationId = QueryFilterParsers.GetString(query, "operationId"),
            ReleaseId = QueryFilterParsers.GetString(query, "releaseId"),
            ChangeSetId = QueryFilterParsers.GetString(query, "changeSetId"),
            Actor = QueryFilterParsers.GetString(query, "actor"),
            ResourceRef = QueryFilterParsers.GetString(query, "resourceRef"),
            PageSize = pageSize ?? 50
        };

        return true;
    }

    private static OperateEventResponse MapEvent(OperateEvent value)
    {
        return new OperateEventResponse
        {
            EventId = value.EventId,
            Kind = ToCamel(value.Kind.ToString()),
            Severity = value.Severity.ToString().ToLowerInvariant(),
            OccurredAt = value.OccurredAt,
            Title = value.Title,
            Summary = value.Summary,
            ServiceId = value.ServiceId,
            LayerId = value.LayerId,
            ObjectId = value.ObjectId,
            Actor = value.Actor,
            CorrelationId = value.CorrelationId,
            TraceId = value.TraceId,
            RequestId = value.RequestId,
            OperationId = value.OperationId,
            ReleaseId = value.ReleaseId,
            ReplicaId = value.ReplicaId,
            ChangeSetId = value.ChangeSetId,
            ResourceRef = value.ResourceRef,
            ProviderLinks = value.ProviderLinks?.Select(link => new OperateProviderLinkResponse
            {
                Provider = link.Provider,
                Label = link.Label,
                Url = link.Url
            }).ToArray(),
            DetailsJson = value.DetailsJson
        };
    }

    private static string ToCamel(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return char.ToLowerInvariant(value[0]) + value[1..];
    }
}
