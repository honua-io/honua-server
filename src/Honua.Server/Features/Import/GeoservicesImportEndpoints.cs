// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;

namespace Honua.Server.Features.Import;

/// <summary>
/// Endpoints for importing data from ArcGIS Server services.
/// </summary>
internal static partial class GeoservicesImportEndpoints
{
    /// <summary>
    /// Map Geoservices import endpoints to the web application with formal API versioning.
    /// </summary>
    public static void MapGeoservicesImportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v{version:apiVersion}/admin/import/geoservices")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("GeoservicesImport")
            .RequireAdminAuthorization();

        // Discover layers from an ArcGIS service URL
        _ = group.MapPost("/discover", HandleDiscoverService)
            .WithName("DiscoverGeoservicesService")
            .WithSummary("Discover available layers from an ArcGIS Server service URL");

        // Start a background import job
        _ = group.MapPost("/start", HandleStartImport)
            .WithName("StartGeoservicesImport")
            .WithSummary("Start importing a layer from an ArcGIS Server service");

        // List active jobs
        _ = group.MapGet("/jobs", HandleListJobs)
            .WithName("ListGeoservicesImportJobs")
            .WithSummary("List active Geoservices import jobs");

        // Get job status
        _ = group.MapGet("/jobs/{jobId}", HandleGetJobStatus)
            .WithName("GetGeoservicesImportJobStatus")
            .WithSummary("Get Geoservices import job status");

        // Cancel job
        _ = group.MapPost("/jobs/{jobId}/cancel", HandleCancelJob)
            .WithName("CancelGeoservicesImportJob")
            .WithSummary("Cancel an Geoservices import job");
    }

    /// <summary>
    /// Discover layers from an ArcGIS Server service URL.
    /// </summary>
    private static async Task HandleDiscoverService(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;

        GeoservicesDiscoverRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(
                GeoservicesImportApiJsonContext.Default.GeoservicesDiscoverRequest,
                cancellationToken);
        }
        catch (Exception ex)
        {
            Log.RequestDeserializationFailed(GetLogger(context), ex);
            await AdminResponseWriter.WriteErrorAsync(context, "Invalid request body", StatusCodes.Status400BadRequest);
            return;
        }

        if (request == null || string.IsNullOrWhiteSpace(request.ServiceUrl))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "ServiceUrl is required", StatusCodes.Status400BadRequest);
            return;
        }

        if (!Uri.TryCreate(request.ServiceUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "ServiceUrl must be a valid HTTP(S) URL", StatusCodes.Status400BadRequest);
            return;
        }

        if (await IsPrivateNetworkAddressAsync(uri))
        {
            await AdminResponseWriter.WriteErrorAsync(context,
                "ServiceUrl resolves to a private or loopback network address, which is not allowed.",
                StatusCodes.Status400BadRequest);
            return;
        }

        try
        {
            var importService = context.RequestServices.GetRequiredService<IGeoservicesImportService>();
            var discoveryRequest = new GeoservicesDiscoveryRequest
            {
                ServiceUrl = request.ServiceUrl,
                TimeoutSeconds = request.TimeoutSeconds ?? 30
            };

            var serviceInfo = await importService.DiscoverServiceAsync(discoveryRequest, cancellationToken);

            var response = new GeoservicesDiscoverResponse
            {
                ServiceUrl = serviceInfo.ServiceUrl,
                ServiceName = serviceInfo.ServiceName,
                Description = serviceInfo.Description,
                SpatialReferenceWkid = serviceInfo.SpatialReferenceWkid,
                MaxRecordCount = serviceInfo.MaxRecordCount,
                Layers = serviceInfo.Layers.Select(l => new GeoservicesLayerSummary
                {
                    Id = l.Id,
                    Name = l.Name,
                    Description = l.Description,
                    GeometryType = l.GeometryType,
                    FeatureCount = l.FeatureCount,
                    HasAttachments = l.HasAttachments
                }).ToArray()
            };

            await Results.Json(response, GeoservicesImportApiJsonContext.Default.GeoservicesDiscoverResponse)
                .ExecuteAsync(context);
        }
        catch (HttpRequestException ex)
        {
            Log.ServiceDiscoveryFailed(GetLogger(context), request.ServiceUrl, ex);
            await AdminResponseWriter.WriteErrorAsync(context,
                "Failed to connect to ArcGIS service.",
                StatusCodes.Status502BadGateway);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            Log.ServiceDiscoveryFailed(GetLogger(context), request.ServiceUrl, ex);
            await AdminResponseWriter.WriteErrorAsync(context,
                "Failed to connect to ArcGIS service.",
                StatusCodes.Status502BadGateway);
        }
        catch (Exception ex)
        {
            Log.ServiceDiscoveryFailed(GetLogger(context), request.ServiceUrl, ex);
            await AdminResponseWriter.WriteErrorAsync(context,
                "Failed to discover service",
                StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Start a background import job.
    /// </summary>
    private static async Task HandleStartImport(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;

        GeoservicesStartImportRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(
                GeoservicesImportApiJsonContext.Default.GeoservicesStartImportRequest,
                cancellationToken);
        }
        catch (Exception ex)
        {
            Log.RequestDeserializationFailed(GetLogger(context), ex);
            await AdminResponseWriter.WriteErrorAsync(context, "Invalid request body", StatusCodes.Status400BadRequest);
            return;
        }

        if (request == null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Request body is required", StatusCodes.Status400BadRequest);
            return;
        }

        // Validate required fields
        if (string.IsNullOrWhiteSpace(request.ServiceUrl))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "ServiceUrl is required", StatusCodes.Status400BadRequest);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.TableName))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "TableName is required", StatusCodes.Status400BadRequest);
            return;
        }

        if (!ImportValidationHelpers.IsValidTableName(request.TableName))
        {
            await AdminResponseWriter.WriteErrorAsync(context,
                "Invalid table name. Use only letters, numbers, and underscores.",
                StatusCodes.Status400BadRequest);
            return;
        }

        if (!Uri.TryCreate(request.ServiceUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "ServiceUrl must be a valid HTTP(S) URL", StatusCodes.Status400BadRequest);
            return;
        }

        if (await IsPrivateNetworkAddressAsync(uri))
        {
            await AdminResponseWriter.WriteErrorAsync(context,
                "ServiceUrl resolves to a private or loopback network address, which is not allowed.",
                StatusCodes.Status400BadRequest);
            return;
        }

        try
        {
            var jobManager = context.RequestServices.GetRequiredService<IDistributedImportJobManager>();

            // Generate job ID
            var jobId = Guid.NewGuid().ToString("N")[..12];

            // Create import request
            var importRequest = new GeoservicesImportRequest
            {
                JobId = jobId,
                ServiceUrl = request.ServiceUrl,
                LayerId = request.LayerId,
                TableName = request.TableName,
                TargetSrid = request.TargetSrid ?? 4326,
                OverwriteExisting = request.OverwriteExisting ?? false,
                WhereClause = request.WhereClause,
                OutputFields = request.OutputFields,
                BatchSize = request.BatchSize,
                RequestTimeoutSeconds = request.RequestTimeoutSeconds ?? 120,
                MaxRetries = request.MaxRetries ?? 3,
                AutoPublish = request.AutoPublish ?? true
            };

            // Store the request
            await jobManager.RequestStore.SetProgressAsync(jobId, importRequest,
                TimeSpan.FromHours(24), cancellationToken);

            // Create initial progress
            var progress = GeoservicesImportProgress.CreateInitial(
                jobId,
                request.ServiceUrl,
                request.LayerId,
                request.TableName);

            await jobManager.ProgressStore.SetProgressAsync(jobId, progress,
                TimeSpan.FromHours(24), cancellationToken);

            // Queue the job
            await jobManager.JobQueue.EnqueueAsync(jobId, cancellationToken);

            Log.ImportJobQueued(GetLogger(context), jobId, request.ServiceUrl, request.LayerId, request.TableName);

            var response = new GeoservicesImportJobResponse
            {
                JobId = jobId,
                Message = "Import job queued for processing",
                StatusUrl = $"jobs/{jobId}",
                CancelUrl = $"jobs/{jobId}/cancel"
            };

            await Results.Json(response, GeoservicesImportApiJsonContext.Default.GeoservicesImportJobResponse,
                    statusCode: StatusCodes.Status202Accepted)
                .ExecuteAsync(context);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Distributed import queue is unavailable", StringComparison.OrdinalIgnoreCase))
        {
            Log.ImportStartFailed(GetLogger(context), request.ServiceUrl, request.LayerId, ex);
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "Distributed import queue is temporarily unavailable. Retry when Redis is healthy.",
                StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex)
        {
            Log.ImportStartFailed(GetLogger(context), request.ServiceUrl, request.LayerId, ex);
            await AdminResponseWriter.WriteErrorAsync(context, "Failed to queue import job", StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Get Geoservices import job status.
    /// </summary>
    private static async Task HandleGetJobStatus(HttpContext context)
    {
        var jobId = context.GetRouteValue("jobId")?.ToString();
        if (string.IsNullOrWhiteSpace(jobId))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Job ID is required", StatusCodes.Status400BadRequest);
            return;
        }

        var cancellationToken = context.RequestAborted;
        var jobManager = context.RequestServices.GetRequiredService<IDistributedImportJobManager>();

        var progress = await jobManager.ProgressStore.GetProgressAsync(jobId, cancellationToken);
        if (progress == null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Import job not found", StatusCodes.Status404NotFound);
            return;
        }

        await Results.Json(progress, GeoservicesImportApiJsonContext.Default.GeoservicesImportProgress)
            .ExecuteAsync(context);
    }

    /// <summary>
    /// Cancel an Geoservices import job.
    /// </summary>
    private static async Task HandleCancelJob(HttpContext context)
    {
        var jobId = context.GetRouteValue("jobId")?.ToString();
        if (string.IsNullOrWhiteSpace(jobId))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Job ID is required", StatusCodes.Status400BadRequest);
            return;
        }

        var cancellationToken = context.RequestAborted;
        var jobManager = context.RequestServices.GetRequiredService<IDistributedImportJobManager>();

        var progress = await jobManager.ProgressStore.GetProgressAsync(jobId, cancellationToken);
        if (progress == null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Import job not found", StatusCodes.Status404NotFound);
            return;
        }

        if (!IsActiveStatus(progress.Status))
        {
            await AdminResponseWriter.WriteErrorAsync(context,
                $"Import job is already {progress.Status}",
                StatusCodes.Status409Conflict);
            return;
        }

        var cancelledProgress = progress with
        {
            Status = GeoservicesImportStatus.Cancelled,
            CompletedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Cancellation requested"
        };

        await jobManager.ProgressStore.SetProgressAsync(jobId, cancelledProgress, TimeSpan.FromHours(24), cancellationToken);
        await jobManager.RequestStore.DeleteProgressAsync(jobId, cancellationToken);

        Log.ImportJobCancelled(GetLogger(context), jobId);

        var response = new GeoservicesImportCancelResponse
        {
            JobId = jobId,
            Message = "Import job cancellation requested"
        };

        await Results.Json(response, GeoservicesImportApiJsonContext.Default.GeoservicesImportCancelResponse)
            .ExecuteAsync(context);
    }

    /// <summary>
    /// List active Geoservices import jobs.
    /// </summary>
    private static async Task HandleListJobs(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;
        var jobManager = context.RequestServices.GetRequiredService<IDistributedImportJobManager>();

        var jobIds = await jobManager.ProgressStore.GetActiveJobIdsAsync(cancellationToken);
        var jobs = new List<GeoservicesImportProgress>(jobIds.Count);

        foreach (var jobId in jobIds)
        {
            var progress = await jobManager.ProgressStore.GetProgressAsync(jobId, cancellationToken);
            if (progress != null && IsActiveStatus(progress.Status))
            {
                jobs.Add(progress);
            }
        }

        var response = new GeoservicesImportJobsResponse { Jobs = jobs.ToArray() };
        await Results.Json(response, GeoservicesImportApiJsonContext.Default.GeoservicesImportJobsResponse)
            .ExecuteAsync(context);
    }

    private static async Task<bool> IsPrivateNetworkAddressAsync(Uri uri)
    {
        System.Net.IPAddress[] addresses;
        try
        {
            addresses = await System.Net.Dns.GetHostAddressesAsync(uri.Host);
        }
        catch (System.Net.Sockets.SocketException)
        {
            // DNS failures are handled downstream by HTTP client error handling (502).
            // Treat unresolved hosts as non-private here to avoid misclassifying them as SSRF attempts.
            return false;
        }

        foreach (var address in addresses)
        {
            if (IsPrivateOrReservedAddress(address))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPrivateOrReservedAddress(System.Net.IPAddress address)
    {
        if (System.Net.IPAddress.IsLoopback(address))
        {
            return true;
        }

        // Map IPv6-mapped IPv4 addresses to their IPv4 equivalent for consistent checking
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();

            // 0.0.0.0/8 (this network)
            if (bytes[0] == 0)
            {
                return true;
            }

            // 10.0.0.0/8
            if (bytes[0] == 10)
            {
                return true;
            }

            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            {
                return true;
            }

            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168)
            {
                return true;
            }

            // 169.254.0.0/16 (link-local, includes cloud metadata 169.254.169.254)
            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return true;
            }

            // 100.64.0.0/10 (carrier-grade NAT)
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
            {
                return true;
            }

            // 198.18.0.0/15 (benchmarking)
            if (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19))
            {
                return true;
            }

            // 224.0.0.0/4 and 240.0.0.0/4 (multicast/reserved)
            if (bytes[0] >= 224)
            {
                return true;
            }
        }
        else if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();

            // ::/128 (unspecified)
            if (address.Equals(System.Net.IPAddress.IPv6None))
            {
                return true;
            }

            // fe80::/10 (IPv6 link-local)
            if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80)
            {
                return true;
            }

            // fc00::/7 (unique local address)
            if ((bytes[0] & 0xfe) == 0xfc)
            {
                return true;
            }

            // ff00::/8 (multicast)
            if (bytes[0] == 0xff)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsActiveStatus(GeoservicesImportStatus status)
        => status is not (GeoservicesImportStatus.Completed or GeoservicesImportStatus.Failed or GeoservicesImportStatus.Cancelled);

    private static ILogger<GeoservicesImportEndpointsLog> GetLogger(HttpContext context) =>
        context.RequestServices.GetRequiredService<ILogger<GeoservicesImportEndpointsLog>>();

    internal sealed class GeoservicesImportEndpointsLog;

    private static partial class Log
    {
        [LoggerMessage(7900, LogLevel.Warning, "Failed to deserialize request")]
        public static partial void RequestDeserializationFailed(ILogger logger, Exception exception);

        [LoggerMessage(7901, LogLevel.Warning, "Service discovery failed for {ServiceUrl}")]
        public static partial void ServiceDiscoveryFailed(ILogger logger, string serviceUrl, Exception exception);

        [LoggerMessage(7902, LogLevel.Information,
            "Import job {JobId} queued: {ServiceUrl} layer {LayerId} to {TableName}")]
        public static partial void ImportJobQueued(
            ILogger logger, string jobId, string serviceUrl, int layerId, string tableName);

        [LoggerMessage(7903, LogLevel.Warning, "Failed to start import for {ServiceUrl} layer {LayerId}")]
        public static partial void ImportStartFailed(ILogger logger, string serviceUrl, int layerId, Exception exception);

        [LoggerMessage(7904, LogLevel.Information, "Import job {JobId} cancelled by user")]
        public static partial void ImportJobCancelled(ILogger logger, string jobId);
    }
}

// Request/Response DTOs for the API

/// <summary>
/// Request to discover an ArcGIS service.
/// </summary>
internal sealed record GeoservicesDiscoverRequest
{
    /// <summary>
    /// URL of the ArcGIS service to discover.
    /// </summary>
    public string? ServiceUrl { get; init; }

    /// <summary>
    /// Timeout in seconds for the discovery request.
    /// </summary>
    public int? TimeoutSeconds { get; init; }
}

/// <summary>
/// Response from service discovery.
/// </summary>
internal sealed record GeoservicesDiscoverResponse
{
    /// <summary>
    /// The service URL.
    /// </summary>
    public required string ServiceUrl { get; init; }

    /// <summary>
    /// Name of the service.
    /// </summary>
    public required string ServiceName { get; init; }

    /// <summary>
    /// Service description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Spatial reference WKID.
    /// </summary>
    public int? SpatialReferenceWkid { get; init; }

    /// <summary>
    /// Maximum records per query.
    /// </summary>
    public int? MaxRecordCount { get; init; }

    /// <summary>
    /// Available layers.
    /// </summary>
    public GeoservicesLayerSummary[] Layers { get; init; } = [];
}

/// <summary>
/// Summary of a layer from discovery.
/// </summary>
internal sealed record GeoservicesLayerSummary
{
    /// <summary>
    /// Layer ID.
    /// </summary>
    public int Id { get; init; }

    /// <summary>
    /// Layer name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Layer description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Geometry type.
    /// </summary>
    public string? GeometryType { get; init; }

    /// <summary>
    /// Estimated feature count.
    /// </summary>
    public int? FeatureCount { get; init; }

    /// <summary>
    /// Whether the layer supports attachments.
    /// </summary>
    public bool HasAttachments { get; init; }
}

/// <summary>
/// Request to start an import job.
/// </summary>
internal sealed record GeoservicesStartImportRequest
{
    /// <summary>
    /// URL of the ArcGIS service.
    /// </summary>
    public string? ServiceUrl { get; init; }

    /// <summary>
    /// ID of the layer to import.
    /// </summary>
    public int LayerId { get; init; }

    /// <summary>
    /// Target table name in PostGIS.
    /// </summary>
    public string? TableName { get; init; }

    /// <summary>
    /// Target SRID (default: 4326).
    /// </summary>
    public int? TargetSrid { get; init; }

    /// <summary>
    /// Whether to overwrite existing table.
    /// </summary>
    public bool? OverwriteExisting { get; init; }

    /// <summary>
    /// Optional WHERE clause to filter features.
    /// </summary>
    public string? WhereClause { get; init; }

    /// <summary>
    /// Optional list of fields to import.
    /// </summary>
    public string[]? OutputFields { get; init; }

    /// <summary>
    /// Batch size for pagination.
    /// </summary>
    public int? BatchSize { get; init; }

    /// <summary>
    /// Request timeout in seconds.
    /// </summary>
    public int? RequestTimeoutSeconds { get; init; }

    /// <summary>
    /// Maximum retry attempts.
    /// </summary>
    public int? MaxRetries { get; init; }

    /// <summary>
    /// Whether to auto-publish the layer.
    /// </summary>
    public bool? AutoPublish { get; init; }
}

/// <summary>
/// Response when a job is queued.
/// </summary>
internal sealed record GeoservicesImportJobResponse
{
    /// <summary>
    /// Job ID.
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    /// Status message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// URL to check job status.
    /// </summary>
    public required string StatusUrl { get; init; }

    /// <summary>
    /// URL to cancel the job.
    /// </summary>
    public required string CancelUrl { get; init; }
}

/// <summary>
/// Response containing active Geoservices import jobs.
/// </summary>
internal sealed record GeoservicesImportJobsResponse
{
    /// <summary>
    /// List of active jobs.
    /// </summary>
    public required GeoservicesImportProgress[] Jobs { get; init; }
}

/// <summary>
/// Response for cancel Geoservices import job request.
/// </summary>
internal sealed record GeoservicesImportCancelResponse
{
    /// <summary>
    /// Job identifier that was cancelled.
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    /// Confirmation message.
    /// </summary>
    public required string Message { get; init; }
}
