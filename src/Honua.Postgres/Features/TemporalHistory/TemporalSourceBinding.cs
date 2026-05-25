// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.TemporalHistory.Abstractions;
using Honua.Core.Features.TemporalHistory.Domain;
using Honua.Postgres.Features.Infrastructure;

namespace Honua.Postgres.Features.TemporalHistory;

/// <summary>
/// Validated, quote-safe binding from a configured layer to its physical temporal source. Every
/// identifier is checked against the safe-identifier allow-list once, here, so SQL builders never
/// interpolate untrusted names.
/// </summary>
internal sealed record TemporalSourceBinding
{
    public required long LayerId { get; init; }

    public required TemporalSourceConfig Config { get; init; }

    /// <summary>Raw, validated schema name, or null when unqualified.</summary>
    public string? Schema { get; init; }

    /// <summary>Raw, validated history/versioned table name.</summary>
    public required string Table { get; init; }

    public required string FeatureIdColumn { get; init; }

    public required string OperationColumn { get; init; }

    public required string ChangedAtColumn { get; init; }

    public string? ActorColumn { get; init; }

    public string? SourceRefColumn { get; init; }

    public string? CorrelationIdColumn { get; init; }

    public string? BeforeAttributesColumn { get; init; }

    public string? AfterAttributesColumn { get; init; }

    public string? GeometryColumn { get; init; }

    public required string SystemPeriodColumn { get; init; }

    public int? Srid { get; init; }

    /// <summary>True when geometry history is recorded and exposed.</summary>
    public bool GeometryEnabled => Config.GeometryHistory && !string.IsNullOrEmpty(GeometryColumn);

    public TemporalSourceKind SourceKind => Config.SourceKind;

    /// <summary>Schema-qualified, double-quoted table identifier safe for direct SQL interpolation.</summary>
    public string QualifiedTable => Schema is null
        ? Quote(Table)
        : $"{Quote(Schema)}.{Quote(Table)}";

    /// <summary>Plain (unquoted) schema.table text used for <c>to_regclass</c> and catalog lookups.</summary>
    public string RegClassText => Schema is null ? Table : $"{Schema}.{Table}";

    /// <summary>Wraps a pre-validated identifier in double quotes.</summary>
    public static string Quote(string identifier) => $"\"{identifier}\"";

    /// <summary>
    /// Resolves and validates a binding for the supplied layer, or returns null when the layer
    /// declares no temporal source.
    /// </summary>
    /// <param name="layer">The configured layer.</param>
    /// <returns>The validated binding, or null when not temporal.</returns>
    /// <exception cref="TemporalHistoryException">Thrown when the temporal configuration is invalid.</exception>
    public static TemporalSourceBinding? Resolve(LayerDefinition layer)
    {
        ArgumentNullException.ThrowIfNull(layer);

        var config = layer.Metadata?.TemporalSource;
        if (config is null)
        {
            return null;
        }

        var storage = layer.StorageMapping
            ?? throw new TemporalHistoryException("Temporal layer is missing a storage mapping.");

        var attribution = config.Attribution;

        var table = config.SourceKind == TemporalSourceKind.AuditLog
            ? (string.IsNullOrWhiteSpace(config.HistoryTableName)
                ? $"{storage.TableName}_history"
                : config.HistoryTableName!)
            : storage.TableName;

        var schema = string.IsNullOrWhiteSpace(storage.SchemaName) ? null : storage.SchemaName;

        return new TemporalSourceBinding
        {
            LayerId = layer.Id,
            Config = config,
            Schema = schema is null ? null : Validate(schema),
            Table = Validate(table),
            FeatureIdColumn = Validate(attribution.FeatureIdColumn),
            OperationColumn = Validate(attribution.OperationColumn),
            ChangedAtColumn = Validate(attribution.ChangedAtColumn),
            ActorColumn = ValidateOptional(attribution.ActorColumn),
            SourceRefColumn = ValidateOptional(attribution.SourceRefColumn),
            CorrelationIdColumn = ValidateOptional(attribution.CorrelationIdColumn),
            BeforeAttributesColumn = ValidateOptional(attribution.BeforeAttributesColumn),
            AfterAttributesColumn = ValidateOptional(attribution.AfterAttributesColumn),
            GeometryColumn = ValidateOptional(attribution.GeometryColumn ?? storage.GeometryColumn),
            SystemPeriodColumn = Validate(string.IsNullOrWhiteSpace(config.SystemPeriodColumn)
                ? "sys_period"
                : config.SystemPeriodColumn!),
            Srid = storage.StorageSrid
        };
    }

    private static string Validate(string identifier)
    {
        if (!SchemaSearchPath.IsValidIdentifier(identifier))
        {
            throw new TemporalHistoryException("Temporal source configuration references an invalid identifier.");
        }

        return identifier;
    }

    private static string? ValidateOptional(string? identifier)
        => string.IsNullOrWhiteSpace(identifier) ? null : Validate(identifier);
}
