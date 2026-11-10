// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Features.Migration.Services;

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
