// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Server.Features.ControlPlane;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for deploy control admin APIs.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(DeployPreflightResponse))]
[JsonSerializable(typeof(DeployPreflightReadiness))]
[JsonSerializable(typeof(DeployPreflightMigration))]
[JsonSerializable(typeof(DeployPreflightDatabaseCompatibility))]
[JsonSerializable(typeof(DeployPlanRequest))]
[JsonSerializable(typeof(CreateDeployOperationRequest))]
[JsonSerializable(typeof(SubmitDeployOperationRequest))]
[JsonSerializable(typeof(RollbackDeployOperationRequest))]
[JsonSerializable(typeof(DeployPlanResponse))]
[JsonSerializable(typeof(DeployPlanTargetResponse))]
[JsonSerializable(typeof(DeployBackendCapabilitiesResponse))]
[JsonSerializable(typeof(DeployOperationResponse))]
[JsonSerializable(typeof(MetadataReleaseContextResponse))]
[JsonSerializable(typeof(MetadataRollbackPlanResponse))]
[JsonSerializable(typeof(MetadataEvidenceRefResponse))]
[JsonSerializable(typeof(DeployRequestFingerprintPayload))]
internal sealed partial class DeployControlJsonContext : JsonSerializerContext
{
}
