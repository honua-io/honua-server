// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.Protocols.Mcp.Models;

/// <summary>
/// AOT-compatible JSON serialization context for MCP wire-format models.
/// All payload types exchanged over the MCP JSON-RPC endpoint must be
/// declared here so <see cref="System.Text.Json"/> can emit source-generated
/// serializers without reflection fallback.
/// </summary>
[JsonSerializable(typeof(McpJsonRpcRequest))]
[JsonSerializable(typeof(McpJsonRpcResponse))]
[JsonSerializable(typeof(List<McpJsonRpcResponse>))]
[JsonSerializable(typeof(McpJsonRpcError))]
[JsonSerializable(typeof(McpErrorData))]
[JsonSerializable(typeof(McpValidationViolation))]
[JsonSerializable(typeof(McpInitializeParams))]
[JsonSerializable(typeof(McpClientInfo))]
[JsonSerializable(typeof(McpInitializeResult))]
[JsonSerializable(typeof(McpServerCapabilities))]
[JsonSerializable(typeof(McpServerInfo))]
[JsonSerializable(typeof(McpToolsListResult))]
[JsonSerializable(typeof(McpToolDescriptor))]
[JsonSerializable(typeof(McpToolsCallParams))]
[JsonSerializable(typeof(McpToolsCallResult))]
[JsonSerializable(typeof(McpContentBlock))]
[JsonSerializable(typeof(McpResourcesListResult))]
[JsonSerializable(typeof(McpResourceDescriptor))]
[JsonSerializable(typeof(McpResourceTemplatesListResult))]
[JsonSerializable(typeof(McpResourceTemplateDescriptor))]
[JsonSerializable(typeof(McpResourcesReadParams))]
[JsonSerializable(typeof(McpResourcesReadResult))]
[JsonSerializable(typeof(McpResourceContent))]
[JsonSerializable(typeof(McpPlanArgument))]
[JsonSerializable(typeof(McpPlanInput))]
[JsonSerializable(typeof(McpPlanStepInput))]
[JsonSerializable(typeof(McpExecutePlanArgument))]
[JsonSerializable(typeof(McpCancelJobArgument))]
[JsonSerializable(typeof(McpValidatePlanOutput))]
[JsonSerializable(typeof(McpDryRunOutput))]
[JsonSerializable(typeof(McpExecuteOutput))]
[JsonSerializable(typeof(McpCancelJobOutput))]
[JsonSerializable(typeof(McpNotImplementedOutput))]
[JsonSerializable(typeof(McpToolErrorOutput))]
[JsonSerializable(typeof(McpJobResource))]
[JsonSerializable(typeof(McpJobResultsResource))]
[JsonSerializable(typeof(McpResultSummary))]
[JsonSerializable(typeof(McpArtifactRef))]
[JsonSerializable(typeof(McpWorkspaceRef))]
[JsonSerializable(typeof(McpProvenance))]
[JsonSerializable(typeof(McpProvenanceSource))]
[JsonSerializable(typeof(McpGeoprocessingError))]
[JsonSerializable(typeof(McpWorkspaceResource))]
[JsonSerializable(typeof(McpProcessCatalogResource))]
[JsonSerializable(typeof(McpProcessEntry))]
[JsonSerializable(typeof(McpHostedProvenance))]
[JsonSerializable(typeof(McpPublishedServiceView))]
[JsonSerializable(typeof(McpPublishedServiceSummary))]
[JsonSerializable(typeof(McpPublishedServiceListView))]
[JsonSerializable(typeof(McpDeploymentView))]
[JsonSerializable(typeof(McpDeploymentSummary))]
[JsonSerializable(typeof(McpDeploymentListView))]
[JsonSerializable(typeof(McpDeploymentTransitionView))]
[JsonSerializable(typeof(McpPackageView))]
[JsonSerializable(typeof(McpPackageSummary))]
[JsonSerializable(typeof(McpPackageListView))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(IReadOnlyList<McpToolDescriptor>))]
[JsonSerializable(typeof(IReadOnlyList<McpResourceDescriptor>))]
[JsonSerializable(typeof(IReadOnlyList<McpContentBlock>))]
[JsonSerializable(typeof(IReadOnlyList<McpResourceContent>))]
[JsonSerializable(typeof(IReadOnlyList<McpValidationViolation>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class McpJsonContext : JsonSerializerContext;
