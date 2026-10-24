// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Alerts.Domain;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for streaming operations admin API models.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ApiResponse<SubscriberListResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(AlertEventEnvelope))]
[JsonSerializable(typeof(SubscriberListResponse))]
[JsonSerializable(typeof(SubscriberInfoResponse))]
[JsonSerializable(typeof(SubscriberInfoResponse[]))]
internal sealed partial class StreamingOperationsJsonContext : JsonSerializerContext
{
}
