// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

/// <summary>
/// Source-generated serialization context for durable operation proposals.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OperationProposal))]
[JsonSerializable(typeof(OperationProposalPlan))]
[JsonSerializable(typeof(OperationProposalAutonomyMetadata))]
[JsonSerializable(typeof(OperationProposalEvidence))]
internal sealed partial class OperationProposalJsonContext : JsonSerializerContext
{
}
