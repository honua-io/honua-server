// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Spec;

internal sealed record GaSpecApplyRequest(string Revision, Guid CommandId);
internal sealed record GaSpecView(
    string PlanId, string? RunId, string Revision, SpecLayerIntent Spec,
    string ActionHash, string VerificationHash, string ObservedHash,
    string Verdict, string Execution, string Evidence, string DraftedBy,
    string? AppliedBy, int? LayerId, IReadOnlyList<string> Blockers);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(SpecLayerIntent))]
[JsonSerializable(typeof(GaSpecApplyRequest))]
[JsonSerializable(typeof(GaSpecView))]
internal sealed partial class GaSpecJsonContext : JsonSerializerContext;
