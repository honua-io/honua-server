// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.WorkflowPackages;

/// <summary>
/// Source-generated JSON context for Console workflow package endpoints.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ApiResponse<WorkflowNodeRegistrySnapshot>))]
[JsonSerializable(typeof(ApiResponse<WorkflowNodeDefinition>))]
[JsonSerializable(typeof(ApiResponse<WorkflowPackage>))]
[JsonSerializable(typeof(ApiResponse<WorkflowPackageListResponse>))]
[JsonSerializable(typeof(ApiResponse<WorkflowPackageVersion>))]
[JsonSerializable(typeof(ApiResponse<WorkflowPackageVersionListResponse>))]
[JsonSerializable(typeof(ApiResponse<WorkflowPackageValidationResult>))]
[JsonSerializable(typeof(ApiResponse<WorkflowDryRunResult>))]
[JsonSerializable(typeof(ApiResponse<WorkflowPublication>))]
[JsonSerializable(typeof(ApiResponse<WorkflowPublicationListResponse>))]
[JsonSerializable(typeof(ApiResponse<WorkflowPublicationRunResult>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(SaveWorkflowPackageRequest))]
[JsonSerializable(typeof(PublishWorkflowPackageRequest))]
[JsonSerializable(typeof(RunWorkflowPublicationRequest))]
[JsonSerializable(typeof(WorkflowNodeRegistrySnapshot))]
[JsonSerializable(typeof(WorkflowNodeProviderSnapshot))]
[JsonSerializable(typeof(WorkflowNodeDefinition))]
[JsonSerializable(typeof(WorkflowNodeParameterSchema))]
[JsonSerializable(typeof(WorkflowNodePortSchema))]
[JsonSerializable(typeof(WorkflowSchemaDefinition))]
[JsonSerializable(typeof(WorkflowNodeCapabilityFlags))]
[JsonSerializable(typeof(WorkflowNodeRuntimeHints))]
[JsonSerializable(typeof(WorkflowPackage))]
[JsonSerializable(typeof(WorkflowPackageVersion))]
[JsonSerializable(typeof(WorkflowGraph))]
[JsonSerializable(typeof(WorkflowNode))]
[JsonSerializable(typeof(WorkflowEdge))]
[JsonSerializable(typeof(WorkflowSchedule))]
[JsonSerializable(typeof(WorkflowPublication))]
[JsonSerializable(typeof(WorkflowPackageValidationResult))]
[JsonSerializable(typeof(WorkflowPackageValidationFailure))]
[JsonSerializable(typeof(WorkflowDryRunResult))]
[JsonSerializable(typeof(WorkflowDryRunLogEntry))]
[JsonSerializable(typeof(WorkflowDryRunArtifact))]
[JsonSerializable(typeof(WorkflowPublicationRunResult))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class WorkflowPackagesJsonContext : JsonSerializerContext
{
}
