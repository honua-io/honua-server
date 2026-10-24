// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Collaboration.Sessions;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(CollaborationJoinRequest))]
[JsonSerializable(typeof(CollaborationJoinResponse))]
[JsonSerializable(typeof(CollaborationLeaveRequest))]
[JsonSerializable(typeof(CollaborationLeaveResponse))]
[JsonSerializable(typeof(CollaborationParticipantWire))]
[JsonSerializable(typeof(CollaborationParticipantWire[]))]
[JsonSerializable(typeof(CollaborationCapabilities))]
[JsonSerializable(typeof(CollaborationSnapshot))]
[JsonSerializable(typeof(CollaborationSessionEvent))]
[JsonSerializable(typeof(CollaborationEventEnvelope))]
[JsonSerializable(typeof(CollaborationEventEnvelope[]))]
[JsonSerializable(typeof(CollaborationOperationWire))]
[JsonSerializable(typeof(CollaborationOperationWire[]))]
[JsonSerializable(typeof(CollaborationCursor))]
[JsonSerializable(typeof(CollaborationSelection))]
[JsonSerializable(typeof(CollaborationFollowTarget))]
[JsonSerializable(typeof(CollaborationBackplaneMessage))]
[JsonSerializable(typeof(CollaborationClientFrame))]
[JsonSerializable(typeof(ApiResponse<CollaborationJoinResponse>))]
[JsonSerializable(typeof(ApiResponse<CollaborationLeaveResponse>))]
internal sealed partial class CollaborationSessionJsonContext : JsonSerializerContext
{
}
