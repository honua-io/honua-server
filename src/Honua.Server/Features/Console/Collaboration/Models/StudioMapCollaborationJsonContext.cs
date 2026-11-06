// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Console.Collaboration.Models;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ApiResponse<StudioMapCommentThreadDto>))]
[JsonSerializable(typeof(ApiResponse<StudioMapCommentThreadListResponse>))]
[JsonSerializable(typeof(ApiResponse<StudioMapActivityListResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(CreateStudioMapCommentThreadRequest))]
[JsonSerializable(typeof(CreateStudioMapCommentReplyRequest))]
[JsonSerializable(typeof(ResolveStudioMapCommentThreadRequest))]
[JsonSerializable(typeof(StudioMapCommentThreadDto))]
[JsonSerializable(typeof(StudioMapCommentThreadListResponse))]
[JsonSerializable(typeof(StudioMapActivityListResponse))]
[JsonSerializable(typeof(StudioMapActivityEntryDto))]
[JsonSerializable(typeof(StudioMapCommentMessageDto))]
internal sealed partial class StudioMapCollaborationJsonContext : JsonSerializerContext
{
}
