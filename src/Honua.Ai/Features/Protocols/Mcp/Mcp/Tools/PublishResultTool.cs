// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// MCP tool that promotes a completed analysis job's materialized feature/table
/// artifact into a hosted service + layer (geospatial-mcp <c>publish_result</c>,
/// honua-server#2482). This is the missing link in the analyze → render chain:
/// analysis results live at <c>honua://jobs/{id}/results</c> and, until now,
/// could not become a queryable/renderable layer.
///
/// <para>
/// The tool does NOT reimplement publishing. It resolves the job's result
/// package through the canonical <see cref="IGeoprocessingJobService"/> (the same
/// service that backs <c>honua://jobs/{id}/results</c>), reads the selected
/// artifact's materialized-table coordinates, then routes them through the very
/// same <c>service.publish</c> operation (<see cref="IOperationInvoker"/>) that
/// <see cref="PublishServiceTool"/> uses — so the operator-approval gate,
/// metadata revision, and multi-outcome (Completed / Queued / RequiresApproval /
/// Denied) contract are shared verbatim. Materialization of the artifact into a
/// table is the analysis job runtime's responsibility; this tool promotes the
/// already-materialized table.
/// </para>
/// </summary>
internal sealed class PublishResultTool : IMcpTool
{
    public const string ToolName = "honua_publish_result";

    /// <summary>
    /// Canonical operation this tool routes through. Mirrors
    /// <c>ServicePublishOperation.OperationId</c>; duplicated as a literal because
    /// <c>Honua.Ai</c> cannot reference the server assembly (dependency direction),
    /// the same reason <see cref="PublishServiceTool.PublishOperationId"/> exists.
    /// </summary>
    public const string PublishOperationId = PublishServiceTool.PublishOperationId;

    /// <summary>
    /// <c>ArtifactRef.Metadata</c> keys the analysis / import runtime records when
    /// it materializes a FeatureLayer/Table artifact into an operational table.
    /// They map 1:1 onto the <c>service.publish</c> parameters, so promotion is a
    /// pure hand-off — the tool invents no materialization path of its own.
    /// </summary>
    private static class MetadataKeys
    {
        public const string ConnectionId = "connectionId";
        public const string Schema = "schema";
        public const string Table = "table";
        public const string GeometryColumn = "geometryColumn";
        public const string GeometryType = "geometryType";
        public const string Srid = "srid";
        public const string PrimaryKey = "primaryKey";
    }

    private const string DeploymentTargetKind = "deployment";

    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<PublishResultTool> _logger;

    public PublishResultTool(IGeoprocessingJobService jobService, ILogger<PublishResultTool> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Lifecycle;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Publish result",
        Description =
            "Promote a completed analysis job's result artifact into a hosted, queryable/renderable layer. "
            + "Chain: run honua_execute_plan → poll honua://jobs/{id} until terminal → call honua_publish_result "
            + "with sourceId set to that job id → the returned serviceId + layerId chain straight into "
            + "honua_query_features and honua_render_map. "
            + "Supported artifact kinds are FeatureLayer and Table (vector/tabular datasets the job materialized "
            + "into a table); Scalar, Raster, File, Report, Map, and AppBundle artifacts are not publishable and "
            + "return a structured error. Large artifacts are promoted by reference — the tool publishes the "
            + "materialized table, never the artifact bytes, so payload size is irrelevant. "
            + "Publishing routes through the same operator-approval gate as honua_publish_service: the result is a "
            + "structured operation handle — Completed carries serviceId + layerId + serviceUri + metadataRevision; "
            + "RequiresApproval carries the approval lane to wait on; Queued carries the durable job id; Denied "
            + "carries the reason.",
        InputSchema = McpToolSchemas.PublishResultArgumentSchema,
        OutputSchema = McpToolOutputSchemas.PublishResultOutputSchema,
        // Write tool: it mutates the catalog by creating a new published layer.
        // Not destructive (creates rather than destroys state). Not idempotent:
        // service.publish does not honor an idempotency key, so a replay publishes
        // a second layer. Mirrors honua_publish_service.
        Annotations = McpToolAnnotationSets.Write("Publish result", destructive: false, idempotent: false)
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("PublishResult");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        var argument = McpToolHelpers.ParseArguments(arguments, McpJsonContext.Default.McpPublishResultArgument);

        var sourceId = argument.SourceId;
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            throw new GeoprocessingValidationException(
                "publish_result requires a 'sourceId' — the completed analysis job whose result artifact is promoted.");
        }

        // The reference implementation materializes the published_service promotion
        // surface. Build App deployments are not yet materialized server-side.
        if (!string.IsNullOrWhiteSpace(argument.TargetKind) &&
            string.Equals(argument.TargetKind, DeploymentTargetKind, StringComparison.OrdinalIgnoreCase))
        {
            throw new GeoprocessingPreconditionFailedException(
                "targetKind 'deployment' (Build App) is not yet materialized by this server; "
                + "use the default 'published_service' promotion.");
        }

        // Resolve the job's result package through the canonical job service — the
        // same seam honua://jobs/{id}/results reads. A missing job surfaces
        // not_found and a non-terminal job surfaces failed_precondition from here.
        var package = await _jobService
            .GetJobResultsAsync(sourceId, principal, cancellationToken)
            .ConfigureAwait(false);

        var artifact = SelectArtifact(package, argument.ArtifactId);
        var (connectionId, schema, table) = RequireMaterializedTable(artifact);

        var invoker = httpContext.RequestServices.GetService<IOperationInvoker>();
        if (invoker is null)
        {
            return McpToolHelpers.SuccessResult(
                new McpPublishResultOutput
                {
                    Status = OperationHandleStatus.Failed.ToString(),
                    RequiresApproval = false,
                    OperationId = PublishOperationId,
                    SourceJobId = sourceId,
                    ArtifactId = artifact.ArtifactId,
                    Message = "The operations toolset is unavailable (no IOperationInvoker is registered in this composition)."
                },
                McpJsonContext.Default.McpPublishResultOutput);
        }

        var request = BuildRequest(argument, artifact, connectionId, schema, table);
        var context = new OperationPolicyContext
        {
            PrincipalId = principal.Identity?.Name
        };

        var handle = await invoker.SubmitAsync(request, context, cancellationToken).ConfigureAwait(false);

        // A completed promotion mutates the promotion-resource catalog (a new
        // honua://published-services/{id} appears) and the capability surface, so
        // fire listChanged to active sessions — mirrors PublishServiceTool
        // (honua-server#1954). Queued/RequiresApproval/Denied did not change the
        // catalog yet, so they emit nothing. Resolved leniently.
        if (handle.Status == OperationHandleStatus.Completed)
        {
            var publisher = httpContext.RequestServices.GetService<IMcpNotificationPublisher>();
            if (publisher is not null)
            {
                publisher.BroadcastResourcesListChanged();
                publisher.BroadcastToolsListChanged();
            }
        }

        return McpToolHelpers.SuccessResult(
            Project(handle, sourceId, artifact.ArtifactId),
            McpJsonContext.Default.McpPublishResultOutput);
    }

    /// <summary>
    /// Selects the artifact to promote: the caller-named <c>artifactId</c> when
    /// provided, otherwise the job's single publishable (FeatureLayer/Table)
    /// artifact. Ambiguity and unsupported kinds surface structured errors.
    /// </summary>
    private static ArtifactRef SelectArtifact(AnalysisResultPackage package, string? artifactId)
    {
        if (!string.IsNullOrWhiteSpace(artifactId))
        {
            var selected = package.Artifacts.FirstOrDefault(a =>
                string.Equals(a.ArtifactId, artifactId, StringComparison.Ordinal))
                ?? throw new GeoprocessingNotFoundException(
                    $"Artifact '{artifactId}' was not found in the result package for the job.");

            RequirePublishableKind(selected);
            return selected;
        }

        var publishable = package.Artifacts.Where(IsPublishable).ToList();
        if (publishable.Count == 0)
        {
            throw new GeoprocessingPreconditionFailedException(
                "The job produced no publishable FeatureLayer or Table artifact; nothing to promote.");
        }

        if (publishable.Count > 1)
        {
            throw new GeoprocessingValidationException(
                "The job produced more than one publishable artifact; specify 'artifactId' to select which to promote.");
        }

        return publishable[0];
    }

    private static bool IsPublishable(ArtifactRef artifact) =>
        artifact.Kind is ArtifactKind.FeatureLayer or ArtifactKind.Table;

    private static void RequirePublishableKind(ArtifactRef artifact)
    {
        if (!IsPublishable(artifact))
        {
            throw new GeoprocessingValidationException(
                $"Artifact '{artifact.ArtifactId}' is a {artifact.Kind} artifact and is not publishable; "
                + "publish_result promotes FeatureLayer or Table artifacts.");
        }
    }

    /// <summary>
    /// Reads the materialized-table coordinates the analysis/import runtime
    /// recorded on the artifact. Their absence means the artifact was never
    /// materialized into an operational table and cannot be promoted.
    /// </summary>
    private static (string ConnectionId, string Schema, string Table) RequireMaterializedTable(ArtifactRef artifact)
    {
        RequirePublishableKind(artifact);

        if (!TryGetMetadata(artifact, MetadataKeys.ConnectionId, out var connectionId) ||
            !TryGetMetadata(artifact, MetadataKeys.Schema, out var schema) ||
            !TryGetMetadata(artifact, MetadataKeys.Table, out var table))
        {
            throw new GeoprocessingPreconditionFailedException(
                $"Artifact '{artifact.ArtifactId}' is not a materialized publishable table "
                + $"(missing '{MetadataKeys.ConnectionId}'/'{MetadataKeys.Schema}'/'{MetadataKeys.Table}' coordinates); "
                + "it cannot be promoted to a hosted layer.");
        }

        return (connectionId, schema, table);
    }

    private static bool TryGetMetadata(ArtifactRef artifact, string key, out string value)
    {
        if (artifact.Metadata.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            value = raw;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static OperationRequest BuildRequest(
        McpPublishResultArgument argument,
        ArtifactRef artifact,
        string connectionId,
        string schema,
        string table)
    {
        var parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["schema"] = schema,
            ["table"] = table,
            ["layerName"] = string.IsNullOrWhiteSpace(argument.LayerName) ? artifact.Label : argument.LayerName,
        };

        if (TryGetMetadata(artifact, MetadataKeys.GeometryColumn, out var geometryColumn))
        {
            parameters["geometryColumn"] = geometryColumn;
        }

        if (TryGetMetadata(artifact, MetadataKeys.GeometryType, out var geometryType))
        {
            parameters["geometryType"] = geometryType;
        }

        if (TryGetMetadata(artifact, MetadataKeys.Srid, out var srid))
        {
            parameters["srid"] = srid;
        }

        if (TryGetMetadata(artifact, MetadataKeys.PrimaryKey, out var primaryKey))
        {
            parameters["primaryKey"] = primaryKey;
        }

        return new OperationRequest
        {
            OperationId = PublishOperationId,
            ConnectionId = connectionId,
            ServiceName = argument.ServiceName,
            Fields = [],
            Parameters = parameters,
        };
    }

    private static McpPublishResultOutput Project(OperationHandle handle, string sourceJobId, string artifactId)
    {
        var output = new McpPublishResultOutput
        {
            Status = handle.Status.ToString(),
            RequiresApproval = handle.Status == OperationHandleStatus.RequiresApproval,
            OperationId = handle.OperationId,
            HandleId = handle.HandleId,
            SourceJobId = sourceJobId,
            ArtifactId = artifactId,
            JobId = handle.JobId,
            ApprovalLane = handle.ApprovalLane,
            MetadataRevision = handle.MetadataRevision,
            Summary = handle.Result?.Summary,
            Message = handle.Reason,
        };

        if (handle.Result is { } result)
        {
            if (result.Details.TryGetValue("layerId", out var layerId))
            {
                output.LayerId = layerId;
            }

            if (result.Details.TryGetValue("serviceName", out var serviceName))
            {
                output.ServiceId = serviceName;
                output.ServiceUri = McpResourceUris.PublishedServiceUri(serviceName);
            }
        }

        return output;
    }
}
