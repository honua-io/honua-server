// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Console.Collaboration.Models;

/// <summary>
/// Source-generated JSON context for the durable Studio map collaboration API
/// (honua-server#1278, slice 1): comment threads + activity feed.
/// </summary>
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
