// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// Base implementation for MCP tools that are contract-first stubs. Returns a
/// structured <see cref="McpNotImplementedOutput"/> with enough information for
/// operators to understand the contract and the unblock path while still
/// exercising authentication, operator-grant authorization, and telemetry.
/// </summary>
internal abstract class NotImplementedToolBase : IMcpTool, IStubMcpTool
{
    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger _logger;

    protected NotImplementedToolBase(IGeoprocessingJobService jobService, ILogger logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    public abstract string Name { get; }

    public abstract string WorkflowFamily { get; }

    /// <summary>
    /// Operator resource type this stub would gate on once implemented. Used to
    /// enforce the same authorization grant the functional tools use so stubs
    /// honor the operator-surface contract even before the backing service ships.
    /// </summary>
    protected abstract OperatorResourceType AuthorizedResource { get; }

    /// <summary>
    /// Operator operation this stub would perform once implemented.
    /// </summary>
    protected abstract OperatorOperation AuthorizedOperation { get; }

    protected abstract string Description { get; }

    protected abstract string BlockedBy { get; }

    protected abstract string Contract { get; }

    protected virtual IReadOnlyList<string> NextSteps { get; } = [];

    public McpToolDescriptor Describe() => new()
    {
        Name = Name,
        Description = Description,
        InputSchema = McpToolSchemas.EmptyObjectSchema
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity(Name);
        McpLog.StubToolInvoked(_logger, Name, BlockedBy);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        await _jobService
            .EnsureCallerAuthorizedAsync(principal, AuthorizedResource, AuthorizedOperation, cancellationToken)
            .ConfigureAwait(false);

        ValidateEmptyObjectArguments(arguments);

        var output = new McpNotImplementedOutput
        {
            Status = "not_implemented",
            Tool = Name,
            BlockedBy = BlockedBy,
            Contract = Contract,
            NextSteps = NextSteps
        };

        return McpToolHelpers.SuccessResult(output, McpJsonContext.Default.McpNotImplementedOutput);
    }

    private static void ValidateEmptyObjectArguments(JsonElement? arguments)
    {
        if (arguments is null || arguments.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }

        if (arguments.Value.ValueKind is not JsonValueKind.Object)
        {
            throw new GeoprocessingValidationException(
                $"Tool arguments must be an object; received '{arguments.Value.ValueKind}'.");
        }

        foreach (var property in arguments.Value.EnumerateObject())
        {
            throw new GeoprocessingValidationException(
                $"Unexpected property '{property.Name}' for tool arguments (schema forbids additional properties).");
        }
    }
}
