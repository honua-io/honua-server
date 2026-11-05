// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

// Registrations stay in sync with the model classes so serialization does not fall through to runtime reflection.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ApiResponse<ComplianceDashboardResponse>))]
[JsonSerializable(typeof(ApiResponse<ComplianceResidencyEvaluationResponse>))]
[JsonSerializable(typeof(ApiResponse<ComplianceKeyRotationResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(ComplianceDashboardResponse))]
[JsonSerializable(typeof(ComplianceResidencyEvaluationRequest))]
[JsonSerializable(typeof(ComplianceResidencyEvaluationResponse))]
[JsonSerializable(typeof(ComplianceKeyRotationResponse))]
[JsonSerializable(typeof(ComplianceSummaryView))]
[JsonSerializable(typeof(ComplianceEncryptionView))]
[JsonSerializable(typeof(ComplianceResidencyView))]
[JsonSerializable(typeof(ComplianceControlView))]
[JsonSerializable(typeof(ComplianceEvidenceView))]
internal sealed partial class ComplianceAdminJsonContext : JsonSerializerContext
{
}
