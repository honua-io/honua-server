// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for GitOps watch APIs.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(GitOpsWatchConfigRequest))]
[JsonSerializable(typeof(GitOpsWatchConfigResponse))]
[JsonSerializable(typeof(GitOpsChangeRecordResponse))]
[JsonSerializable(typeof(GitOpsChangeRecordResponse[]))]
[JsonSerializable(typeof(GitOpsChangeDiffResponse))]
[JsonSerializable(typeof(ApiResponse<GitOpsWatchConfigResponse>))]
[JsonSerializable(typeof(ApiResponse<GitOpsChangeRecordResponse>))]
[JsonSerializable(typeof(ApiResponse<GitOpsChangeRecordResponse[]>))]
[JsonSerializable(typeof(ApiResponse<GitOpsChangeDiffResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
public sealed partial class GitOpsWatchJsonContext : JsonSerializerContext
{
}
