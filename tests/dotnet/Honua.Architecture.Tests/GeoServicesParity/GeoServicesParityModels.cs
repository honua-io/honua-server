// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Architecture.Tests.GeoServicesParity;

internal sealed class GeoServicesParityJoin
{
    public required GeoServicesRoster Roster { get; init; }

    public required ParityJudgment Judgment { get; init; }

    public required string[] ServedButUnclassified { get; init; }

    public required string[] ClassifiedButNotServed { get; init; }

    public required string[] ClassifiedTwice { get; init; }

    public required string[] NotImplementedButServed { get; init; }

    public required string[] MisfiledUnderWrongService { get; init; }

    public required string[] ServiceTypesWithNoMatrixHome { get; init; }

    public required string[] StaleExclusions { get; init; }
}

internal sealed class ParityJudgment
{
    public int SchemaVersion { get; init; }

    public string LastReviewed { get; init; } = string.Empty;

    public string TrackingIssue { get; init; } = string.Empty;

    public string Readme { get; init; } = string.Empty;

    public Dictionary<string, string[]>? StatusVocabulary { get; init; }

    public JudgmentService[]? Services { get; init; }

    [JsonIgnore]
    public Dictionary<string, string[]> Vocabulary => StatusVocabulary ?? [];

    [JsonIgnore]
    public JudgmentService[] ServiceList => Services ?? [];
}

internal sealed class JudgmentService
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Parity { get; init; } = string.Empty;

    public string DrillDownDoc { get; init; } = string.Empty;

    public string[]? ImplementedSurface { get; init; }

    public string[]? KnownGaps { get; init; }

    public Dictionary<string, string[]>? Evidence { get; init; }

    public JudgmentOperation[]? Operations { get; init; }

    public AbsentOperation[]? AbsentOperations { get; init; }

    public Dictionary<string, ParameterCoverageEntry[]>? ParameterCoverage { get; init; }

    public string[]? KnownLimitations { get; init; }

    [JsonIgnore]
    public JudgmentOperation[] OperationList => Operations ?? [];

    [JsonIgnore]
    public AbsentOperation[] AbsentList => AbsentOperations ?? [];

    [JsonIgnore]
    public Dictionary<string, string[]> EvidenceMap => Evidence ?? [];
}

internal sealed class JudgmentOperation
{
    public string Name { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string[]? GeoServicesPaths { get; init; }

    public HonuaExtension? HonuaExtension { get; init; }

    public string Notes { get; init; } = string.Empty;

    [JsonIgnore]
    public string[] PathList => GeoServicesPaths ?? [];
}

internal sealed class AbsentOperation
{
    public string Name { get; init; } = string.Empty;

    public string GeoServicesPath { get; init; } = string.Empty;

    public string Notes { get; init; } = string.Empty;
}

internal sealed class HonuaExtension
{
    public string Extension { get; init; } = string.Empty;

    public string Edition { get; init; } = string.Empty;

    public string FeatureKey { get; init; } = string.Empty;
}

internal sealed class ParameterCoverageEntry
{
    public string Name { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Notes { get; init; }
}

internal sealed class ParityMatrix
{
    public int SchemaVersion { get; init; }

    public string Generator { get; init; } = string.Empty;

    public string TrackingIssue { get; init; } = string.Empty;

    public string LastReviewed { get; init; } = string.Empty;

    public string JudgmentSource { get; init; } = string.Empty;

    public ParityRosterProvenance RouteRoster { get; init; } = new();

    public ParityCanonicalDocs CanonicalDocs { get; init; } = new();

    public Dictionary<string, string[]> StatusVocabulary { get; init; } = [];

    public ParityService[] Services { get; init; } = [];
}

internal sealed class ParityRosterProvenance
{
    public string DerivedFrom { get; init; } = string.Empty;

    public string Normalization { get; init; } = string.Empty;

    public string Note { get; init; } = string.Empty;

    public ParityExclusion[] ExcludedServiceTypes { get; init; } = [];
}

internal sealed class ParityExclusion
{
    public string ServiceType { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;
}

internal sealed class ParityCanonicalDocs
{
    public string LandingPage { get; init; } = string.Empty;

    public string MachineReadableExport { get; init; } = string.Empty;

    public string JudgmentSource { get; init; } = string.Empty;
}

internal sealed class ParityService
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Parity { get; init; } = string.Empty;

    public string DrillDownDoc { get; init; } = string.Empty;

    public string[] ImplementedSurface { get; init; } = [];

    public string[] KnownGaps { get; init; } = [];

    public Dictionary<string, string[]> Evidence { get; init; } = [];

    public ParityOperationBuckets Operations { get; init; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ParityOperation[]? HonuaExtensions { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, ParameterCoverageEntry[]>? ParameterCoverage { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? KnownLimitations { get; init; }
}

internal sealed class ParityOperationBuckets
{
    public ParityOperation[] Implemented { get; init; } = [];

    public ParityOperation[] Partial { get; init; } = [];

    public ParityOperation[] Stub { get; init; } = [];

    public AbsentOperation[] NotImplemented { get; init; } = [];
}

internal sealed class ParityOperation
{
    public string Name { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string[] GeoServicesPaths { get; init; } = [];

    public string[] HonuaEndpoints { get; init; } = [];

    public string[] CapabilityMaturity { get; init; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HonuaExtension? HonuaExtension { get; init; }

    public string Notes { get; init; } = string.Empty;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(ParityMatrix))]
[JsonSerializable(typeof(ParityJudgment))]
internal sealed partial class ParityMatrixJsonContext : JsonSerializerContext
{
}
