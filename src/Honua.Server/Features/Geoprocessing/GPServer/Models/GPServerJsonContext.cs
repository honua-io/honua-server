// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Geoprocessing.GPServer.Models;

/// <summary>
/// AOT-compatible JSON serialization context for GPServer wire-format models.
/// </summary>
[JsonSerializable(typeof(GPServiceInfoResponse))]
[JsonSerializable(typeof(GPTaskReference))]
[JsonSerializable(typeof(GPTaskReference[]))]
[JsonSerializable(typeof(GPTaskInfoResponse))]
[JsonSerializable(typeof(GPParameterInfo))]
[JsonSerializable(typeof(GPParameterInfo[]))]
[JsonSerializable(typeof(GPSubmitJobResponse))]
[JsonSerializable(typeof(GPJobStatusResponse))]
[JsonSerializable(typeof(GPJobResultRef))]
[JsonSerializable(typeof(Dictionary<string, GPJobResultRef>))]
[JsonSerializable(typeof(GPJobMessage))]
[JsonSerializable(typeof(GPJobMessage[]))]
[JsonSerializable(typeof(GPResultResponse))]
[JsonSerializable(typeof(GPExecuteResponse))]
[JsonSerializable(typeof(GPResultResponse[]))]
[JsonSerializable(typeof(string))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class GPServerJsonContext : JsonSerializerContext;
