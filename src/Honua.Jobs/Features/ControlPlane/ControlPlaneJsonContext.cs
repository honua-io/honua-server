// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.ControlPlane;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WorkflowOperationRecord))]
[JsonSerializable(typeof(CanaryRampSpec))]
[JsonSerializable(typeof(MetadataReleaseContext))]
[JsonSerializable(typeof(MetadataRollbackPlan))]
[JsonSerializable(typeof(MetadataEvidenceRef))]
[JsonSerializable(typeof(ExecutionJobRecord))]
[JsonSerializable(typeof(DeployTargetDefinition))]
[JsonSerializable(typeof(ExecutionJobDefinition))]
[JsonSerializable(typeof(ExecutionLogEntry))]
[JsonSerializable(typeof(AnalysisResultPackage))]
internal sealed partial class ControlPlaneJsonContext : JsonSerializerContext
{
}
