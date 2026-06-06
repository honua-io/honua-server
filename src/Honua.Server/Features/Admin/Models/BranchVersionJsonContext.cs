// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source-generation context for the admin branch-version APIs (AOT-safe).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(BranchVersionCreateRequest))]
[JsonSerializable(typeof(BranchVersionResponse))]
[JsonSerializable(typeof(BranchVersionResponse[]))]
[JsonSerializable(typeof(BranchVersionListResponse))]
[JsonSerializable(typeof(ApiResponse<BranchVersionResponse>))]
[JsonSerializable(typeof(ApiResponse<BranchVersionListResponse>))]
public sealed partial class BranchVersionJsonContext : JsonSerializerContext
{
}
