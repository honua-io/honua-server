// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using System.Diagnostics;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.TemporalHistory.Abstractions;
using Honua.Core.Features.TemporalHistory.Domain;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.TemporalHistory;

/// <summary>
/// PostgreSQL temporal-history source. Supports two backend strategies selected by the layer's
/// <see cref="TemporalSourceKind"/>: an append-only audit-log table (<c>{table}_history</c>) and a
/// system-versioned temporal table (<c>tstzrange</c> system period). Both reduce to a shared "as-of
/// set" reconstruction so as-of, diff, and rollback behave consistently. Geometries are returned in
/// the layer's source CRS without reprojection.
/// </summary>
internal sealed class PostgresTemporalHistorySource : ITemporalHistorySource
{
    /// <summary>Affected-feature count above which a rollback is advertised as job-required.</summary>
    private const int JobRequiredThreshold = 1000;

    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<PostgresTemporalHistorySource> _logger;
    private readonly TimeProvider _timeProvider;

    public PostgresTemporalHistorySource(
        IDatabaseConnectionProvider connectionProvider,
        ILogger<PostgresTemporalHistorySource> logger,
        TimeProvider timeProvider)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public async Task<TemporalSourceCapabilityInfo?> GetCapabilitiesAsync(
        LayerDefinition layer,
        CancellationToken cancellationToken = default)
    {
        var binding = TemporalSourceBinding.Resolve(layer);
        if (binding is null)
        {
            return null;
        }

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        NpgsqlConnection connection = lease;

        var isAuditLog = binding.SourceKind != TemporalSourceKind.TemporalTable;
        var (tableExists, asOfIndex) = await ProbeCapabilitiesAsync(connection, binding, cancellationToken)
            .ConfigureAwait(false);

        var warnings = new List<string>();
        if (!tableExists)
        {
            warnings.Add("The configured temporal source is not currently available.");
        }

        // Audit-log as-of reconstruction (DISTINCT ON) requires a supporting (feature_id, changed_at)
        // index to avoid full scans; temporal-table range reads remain supported without one.
        var supportsAsOf = tableExists && (!isAuditLog || asOfIndex);
        if (tableExists && isAuditLog && !asOfIndex)
        {
            warnings.Add("As-of and diff are disabled because the required history index is missing.");
        }

        var attribution = binding.Config.Attribution;
        var supportsAttribution = attribution.ActorColumn is not null
            || attribution.SourceRefColumn is not null
            || attribution.CorrelationIdColumn is not null;
        var supportsRollbackExecution = tableExists
            && supportsAsOf
            && binding.Config.AllowRollback
            && isAuditLog
            && binding.AfterAttributesColumn is not null
            && binding.Config.SchemaEvolution == SchemaEvolutionPolicy.Fixed;

        var capabilities = new TemporalSourceCapabilityInfo
        {
            LayerId = binding.LayerId,
            SupportsAsOf = supportsAsOf,
            SupportsHistory = tableExists,
            SupportsDiff = supportsAsOf,
            SupportsTimeline = tableExists,
            SupportsRollbackPlan = tableExists,
            SupportsRollbackExecution = supportsRollbackExecution,
            SupportsGeometryHistory = binding.GeometryEnabled,
            SupportsAttribution = supportsAttribution,
            SourceKind = binding.SourceKind,
            RetentionPolicy = binding.Config.RetentionPolicy,
            AttributionFields = attribution.AdvertisedFields,
            SchemaEvolution = binding.Config.SchemaEvolution,
            GeometrySrid = binding.Srid,
            Warnings = [.. warnings]
        };

        var sourceKindName = binding.SourceKind.ToString();
        TemporalHistoryLog.Capability(
            _logger, binding.LayerId, sourceKindName, capabilities.SupportsAsOf, capabilities.SupportsDiff);
        return capabilities;
    }

    /// <inheritdoc />
    public async Task<DateTimeOffset?> ResolveCursorAsync(
        LayerDefinition layer,
        TemporalCursor cursor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        if (cursor.Kind == TemporalCursorKind.Timestamp)
        {
            return cursor.Timestamp;
        }

        var binding = TemporalSourceBinding.Resolve(layer);
        if (binding is null)
        {
            return null;
        }

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        NpgsqlConnection connection = lease;
        return await ResolveAsync(connection, binding, cursor, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TemporalCheckpoint>> ListCheckpointsAsync(
        LayerDefinition layer,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var binding = RequireBinding(layer);
        var boundedLimit = limit <= 0 ? TemporalPageRequest.DefaultLimit : Math.Min(limit, TemporalPageRequest.MaxLimit);

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        NpgsqlConnection connection = lease;

        await using var command = new NpgsqlCommand(TemporalHistoryQueryBuilder.Checkpoints(binding), connection);
        AddInt(command, "limit", boundedLimit);

        var checkpoints = new List<TemporalCheckpoint>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var ts = new DateTimeOffset(reader.GetFieldValue<DateTime>(0), TimeSpan.Zero);
            var corr = GetNullableString(reader, 1);
            var src = GetNullableString(reader, 2);
            var label = corr ?? src ?? ts.ToString("O");
            var kind = corr is not null ? "edit-session" : src is not null ? "release" : "timestamp";
            checkpoints.Add(new TemporalCheckpoint
            {
                Cursor = TemporalCursor.AtTimestamp(ts).ToString(),
                Label = label,
                Timestamp = ts,
                Kind = kind
            });
        }

        return checkpoints;
    }

    /// <inheritdoc />
    public async Task<TemporalSnapshot> QueryAsOfAsync(
        LayerDefinition layer,
        TemporalCursor at,
        TemporalPageRequest page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(at);
        var binding = RequireBinding(layer);
        page = (page ?? new TemporalPageRequest()).Normalize();
        var stopwatch = Stopwatch.StartNew();

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        NpgsqlConnection connection = lease;

        var resolved = await ResolveAsync(connection, binding, at, cancellationToken).ConfigureAwait(false)
            ?? throw new TemporalHistoryException("The requested temporal cursor could not be resolved.");

        await using var command = new NpgsqlCommand(TemporalHistoryQueryBuilder.AsOfPage(binding), connection);
        AddTimestamp(command, "at", resolved);
        AddText(command, "cursor", page.Cursor ?? string.Empty);
        AddInt(command, "limit", page.Limit);

        var items = new List<TemporalFeature>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(new TemporalFeature
                {
                    Id = reader.GetString(0),
                    Attributes = TemporalJson.ParseObject(GetNullableString(reader, 1)),
                    Geometry = TemporalJson.ParseElement(GetNullableString(reader, 2))
                });
            }
        }

        var next = items.Count == page.Limit ? items[^1].Id : null;
        var atToken = at.ToString();
        TemporalHistoryLog.AsOfQuery(_logger, binding.LayerId, atToken, items.Count, stopwatch.ElapsedMilliseconds);

        return new TemporalSnapshot
        {
            LayerId = binding.LayerId,
            At = atToken,
            ResolvedAt = resolved,
            GeneratedAt = _timeProvider.GetUtcNow(),
            Srid = binding.Srid,
            Items = items,
            Next = next
        };
    }

    /// <inheritdoc />
    public async Task<TemporalDiff> DiffAsync(
        LayerDefinition layer,
        TemporalCursor fromCursor,
        TemporalCursor toCursor,
        TemporalPageRequest page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fromCursor);
        ArgumentNullException.ThrowIfNull(toCursor);
        var binding = RequireBinding(layer);
        page = (page ?? new TemporalPageRequest()).Normalize();
        var maskAttribution = binding.Config.AccessPolicy?.MaskAttribution ?? false;
        var stopwatch = Stopwatch.StartNew();

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        NpgsqlConnection connection = lease;

        var fromUtc = await ResolveAsync(connection, binding, fromCursor, cancellationToken).ConfigureAwait(false)
            ?? throw new TemporalHistoryException("The 'from' temporal cursor could not be resolved.");
        var toUtc = await ResolveAsync(connection, binding, toCursor, cancellationToken).ConfigureAwait(false)
            ?? throw new TemporalHistoryException("The 'to' temporal cursor could not be resolved.");

        var summary = await ReadDiffSummaryAsync(connection, binding, fromUtc, toUtc, cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(TemporalHistoryQueryBuilder.DiffDetail(binding), connection);
        AddTimestamp(command, "from", fromUtc);
        AddTimestamp(command, "to", toUtc);
        AddText(command, "cursor", page.Cursor ?? string.Empty);
        AddInt(command, "limit", page.Limit);

        var items = new List<TemporalFeatureChange>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(MapFeatureChange(reader, maskAttribution));
            }
        }

        var next = items.Count == page.Limit ? items[^1].FeatureId : null;
        var fromToken = fromCursor.ToString();
        var toToken = toCursor.ToString();
        TemporalHistoryLog.DiffQuery(
            _logger, binding.LayerId, fromToken, toToken,
            summary.Added, summary.Removed, summary.AttributeChanged + summary.GeometryChanged, stopwatch.ElapsedMilliseconds);

        return new TemporalDiff
        {
            LayerId = binding.LayerId,
            From = fromToken,
            To = toToken,
            Summary = summary,
            Items = items,
            Next = next
        };
    }

    /// <inheritdoc />
    public async Task<TemporalTimeline> GetTimelineAsync(
        LayerDefinition layer,
        string featureId,
        TemporalPageRequest page,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(featureId))
        {
            throw new TemporalHistoryException("A feature identifier is required.");
        }

        var binding = RequireBinding(layer);
        page = (page ?? new TemporalPageRequest()).Normalize();
        var maskAttribution = binding.Config.AccessPolicy?.MaskAttribution ?? false;

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        NpgsqlConnection connection = lease;

        await using var command = new NpgsqlCommand(TemporalHistoryQueryBuilder.Timeline(binding), connection);
        AddText(command, "fid", featureId);
        AddNullableTimestamp(command, "cursorTs", ParseInstant(page.Cursor));
        AddInt(command, "limit", page.Limit);

        var revisions = new List<TemporalRevision>();
        DateTimeOffset? lastChangedAt = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var changedAt = new DateTimeOffset(reader.GetFieldValue<DateTime>(0), TimeSpan.Zero);
                lastChangedAt = changedAt;
                var operation = reader.GetString(1);
                var actor = GetNullableString(reader, 2);
                var source = GetNullableString(reader, 3);
                var correlation = GetNullableString(reader, 4);
                var before = TemporalJson.ParseObject(GetNullableString(reader, 5));
                var after = TemporalJson.ParseObject(GetNullableString(reader, 6));
                var geometryChanged = !reader.IsDBNull(7) && reader.GetBoolean(7);

                revisions.Add(new TemporalRevision
                {
                    Cursor = TemporalCursor.AtTimestamp(changedAt).ToString(),
                    Operation = operation,
                    Attribution = BuildAttribution(maskAttribution, actor, source, correlation, changedAt),
                    FieldChanges = TemporalJson.FieldChanges(before, after),
                    GeometryChanged = geometryChanged
                });
            }
        }

        var next = revisions.Count == page.Limit && lastChangedAt is { } last
            ? last.ToString("O")
            : null;

        return new TemporalTimeline
        {
            LayerId = binding.LayerId,
            FeatureId = featureId,
            AttributionMasked = maskAttribution,
            Revisions = revisions,
            Next = next
        };
    }

    /// <inheritdoc />
    public async Task<TemporalRollbackPlan> PlanRollbackAsync(
        LayerDefinition layer,
        TemporalCursor toCursor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toCursor);
        var binding = RequireBinding(layer);
        var validation = new List<TemporalFinding>();
        var compatibility = new List<TemporalFinding>();

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        NpgsqlConnection connection = lease;

        var (tableExists, asOfIndex) = await ProbeCapabilitiesAsync(connection, binding, cancellationToken)
            .ConfigureAwait(false);
        DateTimeOffset? toUtc = null;
        if (tableExists && (binding.SourceKind != TemporalSourceKind.AuditLog || asOfIndex))
        {
            toUtc = await ResolveAsync(connection, binding, toCursor, cancellationToken).ConfigureAwait(false);
        }

        TemporalRollbackMode mode;
        var affected = 0;
        var requiresJob = false;
        var requiresApproval = false;
        var requiresScript = false;

        if (!tableExists)
        {
            mode = TemporalRollbackMode.Blocked;
            validation.Add(Finding("source-unavailable", "error", "The configured temporal source is not available."));
        }
        else if (binding.SourceKind == TemporalSourceKind.AuditLog && !asOfIndex)
        {
            mode = TemporalRollbackMode.Blocked;
            validation.Add(Finding(
                "missing-history-index",
                "error",
                "Rollback requires the configured history index to avoid an unbounded source scan."));
        }
        else if (toUtc is null)
        {
            mode = TemporalRollbackMode.Blocked;
            validation.Add(Finding("unresolvable-cursor", "error", "The target checkpoint could not be resolved."));
        }
        else if (!binding.Config.AllowRollback)
        {
            mode = TemporalRollbackMode.Blocked;
            validation.Add(Finding("rollback-disabled", "error", "Rollback is not enabled for this layer."));
        }
        else if (binding.SourceKind != TemporalSourceKind.AuditLog)
        {
            mode = TemporalRollbackMode.Manual;
            compatibility.Add(Finding(
                "manual-rollback-source", "warning",
                "Rollback for this source kind requires manual operator action."));
        }
        else if (binding.AfterAttributesColumn is null)
        {
            mode = TemporalRollbackMode.Blocked;
            validation.Add(Finding(
                "missing-after-attributes", "error",
                "Rollback requires a recorded post-change attribute column."));
        }
        else
        {
            var now = _timeProvider.GetUtcNow();
            var summary = await ReadDiffSummaryAsync(connection, binding, toUtc.Value, now, cancellationToken).ConfigureAwait(false);
            affected = summary.Added + summary.Removed + summary.AttributeChanged + summary.GeometryChanged;
            requiresApproval = true;
            if (binding.Config.SchemaEvolution != SchemaEvolutionPolicy.Fixed)
            {
                requiresScript = true;
                mode = TemporalRollbackMode.ScriptRequired;
                compatibility.Add(Finding(
                    "schema-evolution-script",
                    "warning",
                    "Rollback across schema-evolved history requires an operator-supplied migration script."));
            }
            else
            {
                requiresJob = true;
                mode = affected > JobRequiredThreshold ? TemporalRollbackMode.JobRequired : TemporalRollbackMode.Supported;
            }

            if (affected == 0)
            {
                validation.Add(Finding("no-op", "info", "The layer already matches the target checkpoint."));
            }

            if (binding.Config.SchemaEvolution != SchemaEvolutionPolicy.Fixed)
            {
                compatibility.Add(Finding(
                    "schema-evolution", "info",
                    "Schema evolution is permitted; verify field compatibility before applying."));
            }
        }

        var toToken = toCursor.ToString();
        var plan = new TemporalRollbackPlan
        {
            LayerId = binding.LayerId,
            To = toToken,
            Mode = mode,
            AffectedCount = affected,
            RequiresApproval = requiresApproval,
            RequiresJob = requiresJob,
            RequiresScript = requiresScript,
            ValidationFindings = validation,
            CompatibilityFindings = compatibility
        };

        var modeName = mode.ToString();
        TemporalHistoryLog.RollbackPlan(_logger, binding.LayerId, toToken, modeName, affected);
        return plan;
    }

    /// <inheritdoc />
    public async Task<TemporalRollbackResult> ExecuteRollbackAsync(
        LayerDefinition layer,
        TemporalCursor toCursor,
        TemporalRollbackContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toCursor);
        ArgumentNullException.ThrowIfNull(context);
        var binding = RequireBinding(layer);

        if (!binding.Config.AllowRollback
            || binding.SourceKind != TemporalSourceKind.AuditLog
            || binding.AfterAttributesColumn is null)
        {
            throw new TemporalHistoryException("Rollback execution is not supported for this layer.");
        }

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        NpgsqlConnection connection = lease;

        var (tableExists, asOfIndex) = await ProbeCapabilitiesAsync(connection, binding, cancellationToken)
            .ConfigureAwait(false);
        if (!tableExists || !asOfIndex)
        {
            throw new TemporalHistoryException("Rollback execution is not supported for this layer.");
        }

        var toUtc = await ResolveAsync(connection, binding, toCursor, cancellationToken).ConfigureAwait(false)
            ?? throw new TemporalHistoryException("The target temporal cursor could not be resolved.");
        var now = _timeProvider.GetUtcNow();

        await using var command = new NpgsqlCommand(TemporalHistoryQueryBuilder.RollbackCorrectiveInsert(binding), connection);
        AddTimestamp(command, "to", toUtc);
        AddTimestamp(command, "now", now);
        AddNullableText(command, "actor", context.Actor);
        AddNullableText(command, "job", context.JobId);
        AddNullableText(command, "corr", context.CorrelationId ?? context.JobId);

        var applied = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var checkpoint = TemporalCursor.AtTimestamp(now).ToString();
        var toToken = toCursor.ToString();

        TemporalHistoryLog.RollbackExecute(_logger, binding.LayerId, context.JobId, toToken, applied, checkpoint);

        return new TemporalRollbackResult
        {
            LayerId = binding.LayerId,
            JobId = context.JobId,
            AppliedCount = applied,
            Checkpoint = checkpoint
        };
    }

    private static async Task<TemporalDiffSummary> ReadDiffSummaryAsync(
        NpgsqlConnection connection,
        TemporalSourceBinding binding,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(TemporalHistoryQueryBuilder.DiffSummary(binding), connection);
        AddTimestamp(command, "from", fromUtc);
        AddTimestamp(command, "to", toUtc);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new TemporalDiffSummary();
        }

        return new TemporalDiffSummary
        {
            Added = (int)reader.GetInt64(0),
            Removed = (int)reader.GetInt64(1),
            AttributeChanged = (int)reader.GetInt64(2),
            GeometryChanged = (int)reader.GetInt64(3)
        };
    }

    private static async Task<(bool TableExists, bool AsOfIndex)> ProbeCapabilitiesAsync(
        NpgsqlConnection connection,
        TemporalSourceBinding binding,
        CancellationToken cancellationToken)
    {
        var isAuditLog = binding.SourceKind != TemporalSourceKind.TemporalTable;
        var primaryColumn = isAuditLog ? binding.FeatureIdColumn : binding.SystemPeriodColumn;
        var secondaryColumn = isAuditLog ? binding.ChangedAtColumn : binding.SystemPeriodColumn;

        await using var command = new NpgsqlCommand(TemporalHistoryQueryBuilder.CapabilityProbe(), connection);
        AddText(command, "reg", binding.RegClassText);
        AddText(command, "primaryColumn", primaryColumn);
        AddText(command, "secondaryColumn", secondaryColumn);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetBoolean(0), reader.GetBoolean(1))
            : (false, false);
    }

    private static async Task<DateTimeOffset?> ResolveAsync(
        NpgsqlConnection connection,
        TemporalSourceBinding binding,
        TemporalCursor cursor,
        CancellationToken cancellationToken)
    {
        if (cursor.Kind == TemporalCursorKind.Timestamp)
        {
            return cursor.Timestamp;
        }

        if (string.IsNullOrWhiteSpace(cursor.Reference))
        {
            return null;
        }

        await using var command = new NpgsqlCommand(TemporalHistoryQueryBuilder.ResolveReference(binding), connection);
        AddText(command, "ref", cursor.Reference);

        var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return scalar is DateTime instant ? new DateTimeOffset(instant, TimeSpan.Zero) : null;
    }

    private static TemporalSourceBinding RequireBinding(LayerDefinition layer)
        => TemporalSourceBinding.Resolve(layer)
            ?? throw new TemporalHistoryException("The layer does not declare a temporal source.");

    private static TemporalFeatureChange MapFeatureChange(DbDataReader reader, bool maskAttribution)
    {
        var featureId = reader.GetString(0);
        var isAdded = reader.GetBoolean(1);
        var isRemoved = reader.GetBoolean(2);
        var fromAttrs = TemporalJson.ParseObject(GetNullableString(reader, 3));
        var toAttrs = TemporalJson.ParseObject(GetNullableString(reader, 4));
        var geometryChanged = !reader.IsDBNull(5) && reader.GetBoolean(5);
        var attributesChanged = !reader.IsDBNull(6) && reader.GetBoolean(6);

        TemporalChangeKind changeKind;
        if (isAdded)
        {
            changeKind = TemporalChangeKind.Added;
        }
        else if (isRemoved)
        {
            changeKind = TemporalChangeKind.Removed;
        }
        else if (attributesChanged)
        {
            changeKind = TemporalChangeKind.AttributeChanged;
        }
        else
        {
            changeKind = TemporalChangeKind.GeometryChanged;
        }

        // For removals the source state holds the attribution; otherwise the target state does.
        var actor = GetNullableString(reader, isRemoved ? 7 : 11);
        var source = GetNullableString(reader, isRemoved ? 8 : 12);
        var correlation = GetNullableString(reader, isRemoved ? 9 : 13);
        var changedAt = GetNullableInstant(reader, isRemoved ? 10 : 14);

        return new TemporalFeatureChange
        {
            FeatureId = featureId,
            ChangeKind = changeKind,
            GeometryChanged = geometryChanged,
            FieldChanges = TemporalJson.FieldChanges(fromAttrs, toAttrs),
            Attribution = BuildAttribution(maskAttribution, actor, source, correlation, changedAt),
            OperationRef = maskAttribution ? null : source ?? correlation
        };
    }

    private static TemporalAttribution? BuildAttribution(
        bool maskAttribution,
        string? actor,
        string? source,
        string? correlation,
        DateTimeOffset? changedAt)
    {
        if (maskAttribution)
        {
            return changedAt is null ? null : new TemporalAttribution { ChangedAt = changedAt };
        }

        if (actor is null && source is null && correlation is null && changedAt is null)
        {
            return null;
        }

        return new TemporalAttribution
        {
            Actor = actor,
            SourceRef = source,
            CorrelationId = correlation,
            ChangedAt = changedAt
        };
    }

    private static TemporalFinding Finding(string code, string severity, string message)
        => new() { Code = code, Severity = severity, Message = message };

    private static DateTimeOffset? ParseInstant(string? value)
        => DateTimeOffset.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed)
            ? parsed
            : null;

    private static string? GetNullableString(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? GetNullableInstant(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : new DateTimeOffset(reader.GetFieldValue<DateTime>(ordinal), TimeSpan.Zero);

    private static void AddText(NpgsqlCommand command, string name, string value)
        => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = value });

    private static void AddNullableText(NpgsqlCommand command, string name, string? value)
        => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value });

    private static void AddInt(NpgsqlCommand command, string name, int value)
        => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Integer) { Value = value });

    private static void AddTimestamp(NpgsqlCommand command, string name, DateTimeOffset value)
        => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = value.UtcDateTime });

    private static void AddNullableTimestamp(NpgsqlCommand command, string name, DateTimeOffset? value)
        => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz)
        {
            Value = value.HasValue ? value.Value.UtcDateTime : DBNull.Value
        });
}
