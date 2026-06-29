// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Ai.Protocols.Mcp.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// MCP tool that publishes a source table as a hosted layer/service through the
/// canonical <c>service.publish</c> operation (#1951). It does NOT reimplement
/// publishing: it adapts the tool arguments onto an
/// <see cref="OperationRequest"/> and routes them through the shared
/// <see cref="IOperationInvoker"/>, which runs the operator-gate policy decision
/// point before reaching <c>ServicePublishExecutor</c> →
/// <c>ILayerPublishingService.PublishLayerAsync</c>. The returned
/// <see cref="OperationHandle"/> is projected into a structured result so the
/// agent can branch on Completed (service URI + metadata revision available),
/// Queued (poll the job), or RequiresApproval (wait on the approval lane).
/// </summary>
internal sealed class PublishServiceTool : IMcpTool
{
    public const string ToolName = "honua_publish_service";

    /// <summary>
    /// Canonical operations-toolset operation id this tool routes through. Mirrors
    /// <c>Honua.Server.Features.Operations.ServicePublishOperation.OperationId</c>;
    /// duplicated as a literal here because <c>Honua.Ai</c> cannot reference the
    /// server assembly (dependency direction).
    /// </summary>
    public const string PublishOperationId = "service.publish";

    private readonly ILogger<PublishServiceTool> _logger;

    public PublishServiceTool(ILogger<PublishServiceTool> logger)
    {
        _logger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Lifecycle;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Publish service",
        Description = "Publish a source table as a hosted layer/service through the canonical service.publish operation and its operator-approval gate. "
            + "Returns a structured operation handle: Completed carries the published service URI, layer id, and metadata revision; "
            + "RequiresApproval carries the approval lane to wait on; Queued carries the durable job id.",
        InputSchema = McpToolSchemas.PublishServiceArgumentSchema,
        OutputSchema = McpToolOutputSchemas.PublishServiceOutputSchema,
        // Write tool: it mutates the catalog by creating a new published layer.
        // Not destructive (it creates rather than destroys state). Not idempotent:
        // service.publish does not honor an idempotency key, so a replay publishes
        // a second layer.
        Annotations = McpToolAnnotationSets.Write("Publish service", destructive: false, idempotent: false)
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("PublishService");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        var argument = McpToolHelpers.ParseArguments(arguments, McpJsonContext.Default.McpPublishServiceArgument);

        var invoker = httpContext.RequestServices.GetService<IOperationInvoker>();
        if (invoker is null)
        {
            return McpToolHelpers.SuccessResult(
                new McpPublishServiceOutput
                {
                    Status = OperationHandleStatus.Failed.ToString(),
                    RequiresApproval = false,
                    OperationId = PublishOperationId,
                    Message = "The operations toolset is unavailable (no IOperationInvoker is registered in this composition)."
                },
                McpJsonContext.Default.McpPublishServiceOutput);
        }

        var request = BuildRequest(argument);
        var context = new OperationPolicyContext
        {
            PrincipalId = principal.Identity?.Name
        };

        var handle = await invoker.SubmitAsync(request, context, cancellationToken).ConfigureAwait(false);

        // honua-server#1954: a completed publish mutates the promotion-resource
        // catalog (a new honua://published-services/{id} appears) and the
        // capability surface, so fire the listChanged notifications to every
        // active streamable-HTTP session. Queued/RequiresApproval/Denied did not
        // change the catalog yet, so they emit nothing. Resolved leniently so a
        // host without the session services keeps working.
        if (handle.Status == OperationHandleStatus.Completed)
        {
            var publisher = httpContext.RequestServices.GetService<IMcpNotificationPublisher>();
            if (publisher is not null)
            {
                publisher.BroadcastResourcesListChanged();
                publisher.BroadcastToolsListChanged();
            }
        }

        return McpToolHelpers.SuccessResult(Project(handle), McpJsonContext.Default.McpPublishServiceOutput);
    }

    private static OperationRequest BuildRequest(McpPublishServiceArgument argument)
    {
        var parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["schema"] = argument.Schema,
            ["table"] = argument.Table,
            ["layerName"] = argument.LayerName,
            ["description"] = argument.Description,
            ["geometryColumn"] = argument.GeometryColumn,
            ["geometryType"] = argument.GeometryType,
            ["primaryKey"] = argument.PrimaryKey,
        };

        if (argument.Srid is { } srid)
        {
            parameters["srid"] = srid.ToString(CultureInfo.InvariantCulture);
        }

        return new OperationRequest
        {
            OperationId = PublishOperationId,
            ConnectionId = argument.ConnectionId,
            ServiceName = argument.ServiceName,
            Fields = argument.Fields ?? [],
            Parameters = parameters,
        };
    }

    private static McpPublishServiceOutput Project(OperationHandle handle)
    {
        var output = new McpPublishServiceOutput
        {
            Status = handle.Status.ToString(),
            RequiresApproval = handle.Status == OperationHandleStatus.RequiresApproval,
            OperationId = handle.OperationId,
            HandleId = handle.HandleId,
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
                output.ServiceName = serviceName;
                output.ServiceUri = McpResourceUris.PublishedServiceUri(serviceName);
            }
        }

        return output;
    }
}
