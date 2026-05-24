// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Server.Features.Admin.Models;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Source-generated JSON serialization for the Console Operate investigation
/// endpoints (#1168).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(InvestigationResponse))]
[JsonSerializable(typeof(InvestigationSummaryResponse))]
[JsonSerializable(typeof(InvestigationPinResponse))]
[JsonSerializable(typeof(InvestigationLinkResponse))]
[JsonSerializable(typeof(InvestigationPageResponse))]
[JsonSerializable(typeof(CreateInvestigationRequest))]
[JsonSerializable(typeof(UpdateInvestigationRequest))]
[JsonSerializable(typeof(AddInvestigationPinRequest))]
[JsonSerializable(typeof(AddInvestigationLinkRequest))]
[JsonSerializable(typeof(IReadOnlyList<InvestigationPinResponse>))]
[JsonSerializable(typeof(IReadOnlyList<InvestigationLinkResponse>))]
[JsonSerializable(typeof(IReadOnlyList<InvestigationSummaryResponse>))]
internal sealed partial class InvestigationJsonContext : JsonSerializerContext
{
}
