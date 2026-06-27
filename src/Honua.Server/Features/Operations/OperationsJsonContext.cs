// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Operations.ServicePublish;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Source-generated JSON context for the Honua Operations Toolset HTTP surface.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ApiResponse<OperationCatalogSnapshot>))]
[JsonSerializable(typeof(ApiResponse<HonuaOperationDefinition>))]
[JsonSerializable(typeof(ApiResponse<OperationResult>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(OperationCatalogSnapshot))]
[JsonSerializable(typeof(OperationProviderSnapshot))]
[JsonSerializable(typeof(HonuaOperationDefinition))]
[JsonSerializable(typeof(OperationPolicyMetadata))]
[JsonSerializable(typeof(OperationGroundingMetadata))]
[JsonSerializable(typeof(OperationResult))]
[JsonSerializable(typeof(PolicyDecision))]
[JsonSerializable(typeof(WorkflowNodeParameterSchema))]
[JsonSerializable(typeof(WorkflowSchemaDefinition))]
[JsonSerializable(typeof(ServicePublishInput))]
[JsonSerializable(typeof(ServicePublishOutput))]
internal sealed partial class OperationsJsonContext : JsonSerializerContext
{
}
