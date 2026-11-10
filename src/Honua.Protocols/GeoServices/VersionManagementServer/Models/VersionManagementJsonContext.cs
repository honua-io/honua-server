// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.VersionManagementServer.Models;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(VersionManagementServiceInfo))]
[JsonSerializable(typeof(VersionInfo))]
[JsonSerializable(typeof(VersionInfo[]))]
[JsonSerializable(typeof(VersionListResponse))]
[JsonSerializable(typeof(VersionInfosResponse))]
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
