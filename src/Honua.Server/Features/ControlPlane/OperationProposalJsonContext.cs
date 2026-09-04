// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
internal sealed partial class OperationProposalJsonContext : JsonSerializerContext
{
}
