// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// AOT-safe JSON serialization context for the reconciliation scorecard (issue #1381). Used to
/// produce the deterministic, canonical byte payload that
/// <see cref="MigrationReconciliationScorecardBuilder.ComputeFingerprint"/> hashes, so the
/// fingerprint is stable across runs and recomputable by verifiers without reflection.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = false)]
[JsonSerializable(typeof(MigrationReconciliationScorecard))]
[JsonSerializable(typeof(MigrationScorecardDataReconciliation))]
[JsonSerializable(typeof(MigrationScorecardCapabilityParity))]
[JsonSerializable(typeof(MigrationScorecardLayer))]
[JsonSerializable(typeof(MigrationScorecardLayer[]))]
public sealed partial class MigrationReconciliationScorecardJsonContext : JsonSerializerContext
{
}
