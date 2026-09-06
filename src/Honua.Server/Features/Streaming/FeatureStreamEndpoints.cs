// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Security;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Capabilities;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Licensing;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Streaming;

/// <summary>
/// Feature-change streaming endpoints supporting WebSocket and SSE transports
/// on a single logical route. Admin endpoints expose session visibility.
/// </summary>
/// <remarks>
/// This file owns the public surface — endpoint mapping, transport dispatch,
/// capabilities, admin endpoints, and shared small helpers. Larger pieces are
/// split across partial-class files in this directory:
///   - FeatureStreamEndpoints.SubscriptionFilter.cs: query-string parsing and validation
///   - FeatureStreamEndpoints.WebSocket.cs: WebSocket transport and control frames
///   - FeatureStreamEndpoints.Sse.cs: SSE transport
///   - FeatureStreamEndpoints.Replay.cs: durable-store replay helpers
/// </remarks>
internal static partial class FeatureStreamEndpoints
{
    private const int SubscriptionBboxSrid = 4326;
    private const string WebSocketTransport = "WebSocket";
    private const string SseTransport = "SSE";

    public static void MapFeatureStreamEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var streamGroup = endpoints.MapGroup("/api/v{version:apiVersion}/streaming")
            // Opt-in Preview until the exact-candidate transport qualification gate passes.
            .WithCapabilityGate("realtime.feature-streams")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Streaming");

        streamGroup.MapGet("/features", HandleFeatureStream)
            .AddEndpointFilter<LiveStreamAuthorizationFilter>()
            .WithDisplayName("Stream Feature Changes")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .WithMetadata(LongLivedStreamEndpointMetadata.Instance)
            .WithMetadata(WebSocketEndpointMetadata.Instance)
            .WithDescription("Opens a WebSocket or SSE stream of real-time feature-change events. " +
                             "WebSocket: send Upgrade header. SSE: send Accept: text/event-stream. " +
                             "Query params: cursor (resume from cursor), clientLabel, serviceId, " +
                             "layerIds/layers (comma-separated layer filter), bbox (WGS84; requires exactly one layer), filter, filter-lang, " +
                             "mode (delta = change-only, default; snapshot = snapshot-then-delta, requires an explicit layer scope).")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status402PaymentRequired)
            .AllowAnonymous();

        streamGroup.MapGet("/features/capabilities", HandleCapabilities)
            .WithDisplayName("Get Feature Stream Capabilities")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .WithDescription("Returns edition-aware feature-stream transport, filter, replay, and per-layer capability metadata.")
            .Produces<ApiResponse<FeatureStreamCapabilitiesResponse>>()
            .AllowAnonymous();

        // Admin endpoints for session visibility
        var adminGroup = endpoints.MapGroup("/api/v{version:apiVersion}/admin/streaming/features")
            .WithCapabilityGate("realtime.feature-streams")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Streaming")
            .RequireAdminAuthorization();

        adminGroup.MapGet("/sessions", HandleListSessions)
            .WithDisplayName("List Feature Stream Sessions")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .Produces<ApiResponse<FeatureStreamStatusResponse>>();

        adminGroup.MapDelete("/sessions/{sessionId:guid}", HandleDisconnectSession)
            .WithDisplayName("Disconnect Feature Stream Session")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Delete]))
            .Produces<ApiResponse<object>>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> HandleFeatureStream(
        [FromServices] FeatureStreamDependencies deps,
        ILogger<FeatureStreamEndpointsLog> logger,
        HttpContext context)
    {
        // Determine transport from request headers.
        var isWebSocket = context.WebSockets.IsWebSocketRequest;
        var accept = context.Request.Headers.Accept.ToString();
        var acceptsSse = accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);
        if (!isWebSocket && !acceptsSse)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "WebSocket upgrade or Accept: text/event-stream header required.");
        }

        var editionError = RequireProEdition(deps, context, logger);
        if (editionError is not null)
        {
            return editionError;
        }

        // Parse and validate subscription filters before accepting the connection.
        var filterResult = await ParseSubscriptionFilterAsync(deps, logger, context).ConfigureAwait(false);
        if (filterResult.Error is not null)
        {
            return filterResult.Error;
        }

        var isAdmin = IsAdmin(context.User);
        if (!filterResult.HasSubscription && (!isWebSocket || isAdmin))
        {
            var adminError = RequireAdminForUnfilteredStream(context);
            if (adminError is not null)
            {
                return adminError;
            }
        }

        // Reserve capacity before snapshot preflight. The preflight can read from backing
        // storage, so admitting the session afterwards would let rejected callers consume
        // unbounded storage work outside MaxConcurrentSessions.
        var addDefaultSubscription = !isWebSocket || filterResult.HasSubscription || isAdmin;
        var session = deps.SessionManager.TryCreateSession(
            isWebSocket ? WebSocketTransport : SseTransport,
            NullIfEmpty(context.Request.Query["clientLabel"].ToString()),
            filterResult.Filter,
            addDefaultSubscription,
            ResolveAdmissionPartition(context));
        if (session is null)
        {
            return CreateSessionLimitExceeded(context, deps.Options.Value.MaxConcurrentSessions);
        }

        using var sessionLease = session;

        // Snapshot servability is decided here, ahead of the transport handshake: it is the last
        // point at which an unservable baseline can still be reported as a typed problem document
        // rather than as a dead stream (honua-server#3181 REQ-002).
        var snapshotError = await ValidateSnapshotServabilityAsync(
            deps,
            logger,
            context,
            // A WebSocket upgrade wins even when its request also advertises SSE.
            isSse: !isWebSocket,
            filterResult.Mode,
            filterResult.Filter).ConfigureAwait(false);
        if (snapshotError is not null)
        {
            return snapshotError;
        }

        if (isWebSocket)
        {
            await HandleWebSocketStream(
                deps,
                logger,
                context,
                session,
                filterResult.Filter,
                addDefaultSubscription,
                announceInitialSubscription: filterResult.HasSubscription,
                filterResult.Mode).ConfigureAwait(false);
            return Results.Empty;
        }

        await HandleSseStream(
            deps,
            logger,
            context,
            session,
            filterResult.Filter,
            filterResult.Mode).ConfigureAwait(false);
        return Results.Empty;
    }

    private static async Task<IResult> HandleCapabilities(
        [FromServices] FeatureStreamDependencies deps,
        [FromServices] DeploymentIdentity deploymentIdentity,
        [FromServices] Conformance.FeatureStreamConformanceRunRegistry conformance,
        HttpContext context)
    {
        var options = deps.Options.Value;
        var streamDecision = LicenseGate.CheckEntitlement(
            context.RequestServices,
            "streaming.feature-subscriptions");
        var edition = streamDecision.Edition;
        var enabled = streamDecision.IsActive;
        var snapshot = await deps.MetadataV2GraphProvider.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
        var layerCapabilities = ResolveAllStreamLayers(snapshot)
            .Where(layer =>
                AccessPolicyHelpers.IsResourceAccessible(
                    context,
                    layer.Resource,
                    layer.Service))
            .Select(layer =>
            {
                string[]? temporalFieldNames = null;
                var temporalFields = layer.Resource.ReadTemporalFields();
                var configuredFields = new[] { temporalFields.StartTimeField, temporalFields.EndTimeField }
                    .Where(field => !string.IsNullOrWhiteSpace(field))
                    .Select(field => field!)
                    .ToArray();
                temporalFieldNames = configuredFields is { Length: > 0 } ? configuredFields : null;
                var supportsTemporalFilters = TemporalExtentHelpers.HasOptInTemporalFields(layer.Resource);
                var srid = layer.Resource.ReadSrid() ?? 4326;

                return new FeatureStreamLayerCapability
                {
                    LayerId = layer.LayerId,
                    Name = layer.Resource.Metadata.Name,
                    CanSubscribe = enabled,
                    SupportsSpatialFilters = layer.Resource.ReadGeometryType() is not MetadataV2GeometryType.None,
                    SupportsTemporalFilters = supportsTemporalFilters,
                    TemporalFields = temporalFieldNames,
                    Crs = $"EPSG:{srid.ToString(CultureInfo.InvariantCulture)}"
                };
            })
            .ToArray();

        var response = new FeatureStreamCapabilitiesResponse
        {
            Enabled = enabled,
            Edition = edition.ToString(),
            MinimumEdition = HonuaEdition.Pro.ToString(),
            Transports = enabled ? ["websocket", "sse"] : [],
            FilterFamilies = enabled
                ? ["layer", "bbox", "attribute", "temporal"]
                : [],
            ReplaySupported = enabled,
            CursorRetentionLimit = deps.EventOptions.Value.MaxRetainedEvents,
            HeartbeatIntervalSeconds = options.HeartbeatInterval.TotalSeconds,
            MaxConcurrentSessions = options.MaxConcurrentSessions,
            DeleteBeforeImages = enabled,
            Modes = enabled ? [DeltaModeValue, SnapshotModeValue, SnapshotThenDeltaModeValue] : [],
            SubscriptionSequence = enabled,
            MaxSnapshotFeatures = options.MaxSnapshotFeatures,
            MaxSnapshotScanRows = options.MaxSnapshotScanRows,
            MaxSnapshotBytes = options.MaxSnapshotBytes,
            // The mutable release version and the immutable deployment revision are separate
            // fields: evidence bound to a deployment must reference the revision (#3038).
            ServerVersion = deploymentIdentity.ReleaseVersion,
            DeploymentRevision = deploymentIdentity.Revision,
            DeploymentRevisionSource = deploymentIdentity.RevisionSource,
            // Published under both names on purpose: see FeatureStreamCapabilitiesResponse.ServerRevision.
            ServerRevision = deploymentIdentity.Revision,
            Conformance = conformance.DescribeCapability(),
            Layers = layerCapabilities
        };

        return Results.Json(
            ApiResponse<FeatureStreamCapabilitiesResponse>.CreateSuccess(response),
            FeatureStreamJsonContext.Default.ApiResponseFeatureStreamCapabilitiesResponse);
    }

    private static IResult HandleListSessions(
        [FromServices] FeatureStreamSessionManager sessionManager,
        ILogger<FeatureStreamEndpointsLog> logger)
    {
        var sessions = sessionManager.GetSessions();
        var now = DateTimeOffset.UtcNow;

        var sessionResponses = sessions.Select(s => new FeatureStreamSessionResponse
        {
            SessionId = s.SessionId,
            ConnectedAt = s.ConnectedAt,
            ClientLabel = s.ClientLabel,
            Transport = s.Transport,
            LastQueuedCursor = s.LastQueuedCursor,
            DurationSeconds = (now - s.ConnectedAt).TotalSeconds,
            HasFilter = s.HasFilter,
            FilterSummary = s.FilterSummary,
            ServiceIdFilter = s.ServiceIdFilter,
            LayerIdFilter = s.LayerIdFilter
        }).ToArray();

        var wsSessions = sessionResponses.Count(s => s.Transport == WebSocketTransport);
        var sseSessions = sessionResponses.Count(s => s.Transport == SseTransport);

        var response = new FeatureStreamStatusResponse
        {
            ActiveSessions = sessionResponses.Length,
            WebSocketSessions = wsSessions,
            SseSessions = sseSessions,
            SlowConsumerDrops = sessionManager.SlowConsumerDrops,
            HeartbeatsSent = sessionManager.HeartbeatsSent,
            Sessions = sessionResponses,
            GeneratedAt = now
        };

        return Results.Json(
            ApiResponse<FeatureStreamStatusResponse>.CreateSuccess(response),
            FeatureStreamJsonContext.Default.ApiResponseFeatureStreamStatusResponse);
    }

    private static IResult HandleDisconnectSession(
        Guid sessionId,
        [FromServices] FeatureStreamSessionManager sessionManager,
        HttpContext context,
        ILogger<FeatureStreamEndpointsLog> logger)
    {
        if (!sessionManager.DisconnectSession(sessionId))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status404NotFound,
                string.Concat("Feature stream session ", sessionId.ToString(), " not found."));
        }

        return Results.Json(
            ApiResponse<object>.SuccessWithMessage(string.Concat("Session ", sessionId.ToString(), " disconnected.")),
            FeatureStreamJsonContext.Default.ApiResponseObject);
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    internal static string ResolveAdmissionPartition(HttpContext context)
    {
        var tenant = context.RequestServices.GetService<ITenantContext>()?.TenantId;
        if (!string.IsNullOrWhiteSpace(tenant))
        {
            return $"tenant:{tenant}";
        }

        var actor = CanonicalSecurityActor.Resolve(context.User);
        return actor is null ? "anonymous" : $"principal:{actor.ActorId}";
    }

    private static IResult CreateSessionLimitExceeded(HttpContext context, int maxConcurrentSessions)
        => ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status503ServiceUnavailable,
                $"Feature stream session limit of {maxConcurrentSessions} concurrent sessions reached.");
}

/// <summary>
/// Log category for feature stream endpoints.
/// </summary>
internal sealed class FeatureStreamEndpointsLog;
