// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// AOT-friendly JSON source-generation context for the compliance admin surface.
/// Keep registrations in sync with the model class to avoid runtime reflection paths.
/// </summary>
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
