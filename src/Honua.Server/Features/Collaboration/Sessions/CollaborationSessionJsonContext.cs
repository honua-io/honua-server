// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.Collaboration.Sessions;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(CollaborationJoinRequest))]
[JsonSerializable(typeof(CollaborationJoinResponse))]
[JsonSerializable(typeof(CollaborationParticipantPresence))]
[JsonSerializable(typeof(CollaborationParticipantPresence[]))]
[JsonSerializable(typeof(CollaborationSessionSnapshot))]
[JsonSerializable(typeof(CollaborationEventEnvelope))]
[JsonSerializable(typeof(CollaborationEventEnvelope[]))]
[JsonSerializable(typeof(CollaborationCursor))]
[JsonSerializable(typeof(CollaborationSelection))]
[JsonSerializable(typeof(CollaborationFollowState))]
[JsonSerializable(typeof(ApiResponse<CollaborationJoinResponse>))]
internal sealed partial class CollaborationSessionJsonContext : JsonSerializerContext
{
}
