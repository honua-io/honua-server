// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.VersionManagementServer.Models;

/// <summary>
/// Source-generated JSON serialization context for VersionManagementServer DTOs (AOT-compatible,
/// camelCase to match the Esri wire shape).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(VersionManagementServiceInfo))]
[JsonSerializable(typeof(VersionInfo))]
[JsonSerializable(typeof(VersionInfo[]))]
[JsonSerializable(typeof(VersionListResponse))]
[JsonSerializable(typeof(CreateVersionResponse))]
[JsonSerializable(typeof(VersionMomentResponse))]
[JsonSerializable(typeof(VersionConflictFieldDiffInfo))]
[JsonSerializable(typeof(VersionConflictFieldDiffInfo[]))]
[JsonSerializable(typeof(VersionConflictInfo))]
[JsonSerializable(typeof(VersionConflictInfo[]))]
[JsonSerializable(typeof(ReconcileResponse))]
[JsonSerializable(typeof(InspectConflictsResponse))]
[JsonSerializable(typeof(ResolveConflictsResponse))]
[JsonSerializable(typeof(PostResponse))]
[JsonSerializable(typeof(VersionJobResponse))]
internal sealed partial class VersionManagementJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
