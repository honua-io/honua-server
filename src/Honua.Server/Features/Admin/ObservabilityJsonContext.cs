// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Server.Features.Admin.Models;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Source-generated JSON serialization for the Console Operate observability
/// endpoints (#1168). All DTOs declared here so AOT trimming preserves their
/// metadata.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ObservabilityAlertEventResponse))]
[JsonSerializable(typeof(ObservabilityAlertEventPageResponse))]
[JsonSerializable(typeof(ObservabilityAlertAcknowledgeRequest))]
[JsonSerializable(typeof(ObservabilityAlertSuppressRequest))]
[JsonSerializable(typeof(ObservabilityAlertResolveRequest))]
[JsonSerializable(typeof(ObservabilityAuditRecordResponse))]
[JsonSerializable(typeof(ObservabilityAuditPageResponse))]
[JsonSerializable(typeof(OperateEventResponse))]
[JsonSerializable(typeof(OperateEventPageResponse))]
[JsonSerializable(typeof(OperateProviderLinkResponse))]
[JsonSerializable(typeof(OperateLogEntryResponse))]
[JsonSerializable(typeof(OperateLogPageResponse))]
[JsonSerializable(typeof(IReadOnlyList<OperateEventResponse>))]
[JsonSerializable(typeof(IReadOnlyList<OperateProviderLinkResponse>))]
[JsonSerializable(typeof(IReadOnlyList<OperateLogEntryResponse>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSerializable(typeof(IReadOnlyList<ObservabilityAlertEventResponse>))]
[JsonSerializable(typeof(IReadOnlyList<ObservabilityAuditRecordResponse>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(string))]
internal sealed partial class ObservabilityJsonContext : JsonSerializerContext
{
}
