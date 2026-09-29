// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Geoprocessing.Execution;

/// <summary>Executes durable route and service-area jobs through the shared routing provider.</summary>
internal sealed partial class RoutingJobExecutor(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<GeoprocessingExecutorOptions> options,
    ILogger<RoutingJobExecutor> logger) : IProcessExecutor
{
    public IReadOnlySet<string> ProcessIds { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        RoutingProcessDefinitions.Route,
        RoutingProcessDefinitions.ServiceArea,
    };

    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

    public async Task<JobExecutionResult> ExecuteAsync(ExecutionJobRecord job, IJobExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var processId = GeoprocessingDispatchHelper.ResolveProcessId(job.Spec.Parameters);
        if (processId is null || !ProcessIds.Contains(processId))
        {
            return JobExecutionResult.Failed("Unsupported routing process.") with { IsRetryable = false };
        }
        var inputs = new StepInputReader(job.Spec.Parameters);
        if (!inputs.TryGet("request", out var requestJson) || string.IsNullOrWhiteSpace(requestJson)
            || System.Text.Encoding.UTF8.GetByteCount(requestJson) > options.CurrentValue.MaxArtifactBytes)
        {
            return JobExecutionResult.Failed("A bounded canonical routing request is required.") with { IsRetryable = false };
        }

        try
        {
            // Executors are singletons; the selected database-backed provider is scoped.
            // Resolve it only for a routing job, so non-routing worker hosts remain usable.
            using var scope = scopeFactory.CreateScope();
            var provider = scope.ServiceProvider.GetService<IRoutingProvider>();
            if (provider is null)
            {
                return JobExecutionResult.Failed("No routing provider is configured on this worker.");
            }
            var configuration = scope.ServiceProvider.GetRequiredService<IOptions<RoutingConfiguration>>().Value;
            var capabilities = await provider.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            byte[] features;
            bool solved;
            if (processId == RoutingProcessDefinitions.Route)
            {
                var request = JsonSerializer.Deserialize(requestJson, RoutingJobJsonContext.Default.RouteJobRequest)?.ToCanonicalRequest();
                var error = request is null ? "A route request is required." : RoutingRequestValidation.ValidateRoute(request, configuration, capabilities);
                if (error is not null)
                {
                    return JobExecutionResult.Failed(error) with { IsRetryable = false };
                }
                await context.ReportProgressAsync(10, "Solving route", cancellationToken).ConfigureAwait(false);
                var result = await provider.SolveRouteAsync(request!, cancellationToken).ConfigureAwait(false);
                solved = result.Solved;
                features = WriteRoute(result);
            }
            else
            {
                var request = JsonSerializer.Deserialize(requestJson, RoutingJobJsonContext.Default.ServiceAreaJobRequest)?.ToCanonicalRequest();
                var error = request is null ? "A service-area request is required." : RoutingRequestValidation.ValidateServiceArea(request, configuration, capabilities);
                if (error is not null)
                {
                    return JobExecutionResult.Failed(error) with { IsRetryable = false };
                }
                await context.ReportProgressAsync(10, "Solving service areas", cancellationToken).ConfigureAwait(false);
                var result = await provider.SolveServiceAreaAsync(request!, cancellationToken).ConfigureAwait(false);
                solved = result.Polygons.Count > 0;
                features = WriteServiceAreas(result);
            }
            if (features.LongLength > options.CurrentValue.MaxArtifactBytes)
            {
                return JobExecutionResult.Failed("Routing output exceeds the configured artifact size limit.") with { IsRetryable = false };
            }

            cancellationToken.ThrowIfCancellationRequested();
            await context.PublishArtifactAsync(FeatureCollectionArtifact.BuildDataUri(features), cancellationToken).ConfigureAwait(false);
            await context.PublishArtifactAsync(solved ? "data:application/json;base64,dHJ1ZQ==" : "data:application/json;base64,ZmFsc2U=", cancellationToken).ConfigureAwait(false);
            await context.ReportProgressAsync(100, "Routing completed", cancellationToken).ConfigureAwait(false);
            return JobExecutionResult.Succeeded();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException)
        {
            return JobExecutionResult.Failed("The canonical routing request or result is invalid.") with { IsRetryable = false };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.ExecutionFailed(logger, job.OperationId, exception);
            return JobExecutionResult.Failed("The configured routing provider could not complete the job.");
        }
    }

    private static byte[] WriteRoute(RouteSolveResult result)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            StartCollection(writer);
            if (result.Solved)
            {
                StartFeature(writer, 1, result.RouteGeometryGeoJson);
                writer.WriteNumber("totalLengthMeters", result.TotalLengthMeters);
                writer.WriteNumber("totalTimeMinutes", result.TotalTimeMinutes);
                EndFeature(writer);
            }
            EndCollection(writer);
        }
        return buffer.ToArray();
    }

    private static byte[] WriteServiceAreas(ServiceAreaSolveResult result)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            StartCollection(writer);
            var id = 1;
            foreach (var polygon in result.Polygons)
            {
                StartFeature(writer, id++, polygon.GeometryGeoJson);
                writer.WriteNumber("facilityId", polygon.FacilityId);
                writer.WriteNumber("fromBreak", polygon.FromBreak);
                writer.WriteNumber("toBreak", polygon.ToBreak);
                EndFeature(writer);
            }
            EndCollection(writer);
        }
        return buffer.ToArray();
    }

    private static void StartCollection(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "FeatureCollection");
        writer.WriteStartArray("features");
    }

    private static void EndCollection(Utf8JsonWriter writer)
    {
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void StartFeature(Utf8JsonWriter writer, int id, string geometry)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "Feature");
        writer.WriteNumber("id", id);
        writer.WritePropertyName("geometry");
        using var document = JsonDocument.Parse(geometry);
        document.RootElement.WriteTo(writer);
        writer.WriteStartObject("properties");
    }

    private static void EndFeature(Utf8JsonWriter writer)
    {
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static partial class Log
    {
        [LoggerMessage(9844, LogLevel.Error, "Routing job {OperationId} failed")]
        internal static partial void ExecutionFailed(ILogger logger, string operationId, Exception exception);
    }
}
