// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Warehouse.Domain;

public sealed record WarehouseQueryGuardrails
{
    public const int DefaultMaxRows = 10_000;

    public const int DefaultMaxH3Cells = 50_000;

    public const int DefaultCommandTimeoutSeconds = 60;

    public static readonly WarehouseQueryGuardrails Default = new();

    public int MaxRows { get; init; } = DefaultMaxRows;

    public int MaxH3Cells { get; init; } = DefaultMaxH3Cells;

    public int CommandTimeoutSeconds { get; init; } = DefaultCommandTimeoutSeconds;

    public long? MaxScanBytes { get; init; }

    public bool EnforceReadOnly { get; init; } = true;

    public void Validate()
    {
        if (MaxRows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRows), "MaxRows must be positive.");
        }

        if (MaxH3Cells <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxH3Cells), "MaxH3Cells must be positive.");
        }

        if (CommandTimeoutSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(CommandTimeoutSeconds), "CommandTimeoutSeconds must be positive.");
        }

        if (MaxScanBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxScanBytes), "MaxScanBytes must be positive when specified.");
        }
    }
}

public sealed record WarehousePushedPredicate(
    string Kind,
    bool PushedDown,
    string Summary);

public sealed record WarehouseRedactedParameter(
    string Placeholder,
    string ValueType,
    string RedactedValue);

public sealed record WarehouseQueryPlanRequest
{
    public required string ProviderName { get; init; }

    public required string SourceId { get; init; }

    public required string TargetTableOrView { get; init; }

    public required FeatureQuery Query { get; init; }

    public H3AggregationQuery? H3Query { get; init; }

    public int? LayerSrid { get; init; }

    public string? BboxColumn { get; init; }

    public string? ExternalSourceUri { get; init; }

    public WarehouseQueryGuardrails? Guardrails { get; init; }
}

public sealed record WarehouseQueryPlan
{
    public required string PlanHash { get; init; }

    public required string ProviderName { get; init; }

    public required string SourceId { get; init; }

    public required string OperationKind { get; init; }

    public required string RedactedSql { get; init; }

    public required ImmutableArray<WarehouseRedactedParameter> Parameters { get; init; }

    public required ImmutableArray<WarehousePushedPredicate> Predicates { get; init; }

    public required bool BboxPushdown { get; init; }

    public required bool H3Pushdown { get; init; }

    public required int EffectiveRowLimit { get; init; }

    public int? EffectiveMaxH3Cells { get; init; }

    public required WarehouseQueryGuardrails AppliedGuardrails { get; init; }

    public required bool GuardrailCapped { get; init; }

    public required bool ReadOnly { get; init; }

    public ImmutableArray<string> RequiredExtensions { get; init; } = [];

    public ImmutableArray<string> Warnings { get; init; } = [];

    public string ToRedactedJson()
        => JsonSerializer.Serialize(this, WarehouseJsonContext.Default.WarehouseQueryPlan);

    public override string ToString()
        => $"WarehouseQueryPlan {{ ProviderName = {ProviderName}, SourceId = {SourceId}, OperationKind = {OperationKind}, PlanHash = {PlanHash} }}";
}
