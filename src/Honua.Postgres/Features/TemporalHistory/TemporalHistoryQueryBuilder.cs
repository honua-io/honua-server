// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.TemporalHistory.Domain;

namespace Honua.Postgres.Features.TemporalHistory;

/// <summary>
/// Builds parameterized SQL for temporal-history reads (as-of, diff, timeline, checkpoints) and the
/// append-only corrective rollback insert. Identifiers are pre-validated and quoted by
/// <see cref="TemporalSourceBinding"/>; all values flow through bound parameters. The same "as-of set"
/// sub-select underpins as-of, diff, and rollback so both backend strategies share one reconstruction
/// definition.
/// </summary>
internal static class TemporalHistoryQueryBuilder
{
    /// <summary>Deletion sentinel recorded by audit-log sources.</summary>
    private const string DeleteOperation = "DELETE";

    /// <summary>
    /// Builds the "as-of set" sub-select that yields one row per live feature at the supplied
    /// timestamp parameter, with columns <c>fid</c> (native), <c>attrs</c> (jsonb), <c>geom</c>
    /// (geometry or null), and <c>geom_json</c> (GeoJSON text or null).
    /// </summary>
    public static string AsOfSet(TemporalSourceBinding b, string atParam)
        => b.SourceKind == TemporalSourceKind.TemporalTable
            ? AsOfSetTemporalTable(b, atParam)
            : AsOfSetAuditLog(b, atParam);

    private static string AsOfSetAuditLog(TemporalSourceBinding b, string atParam)
    {
        var fid = TemporalSourceBinding.Quote(b.FeatureIdColumn);
        var changed = TemporalSourceBinding.Quote(b.ChangedAtColumn);
        var op = TemporalSourceBinding.Quote(b.OperationColumn);
        var attrs = b.AfterAttributesColumn is null
            ? "'{}'::jsonb"
            : $"h.{TemporalSourceBinding.Quote(b.AfterAttributesColumn)}";
        var geomInner = b.GeometryEnabled
            ? $"h.{TemporalSourceBinding.Quote(b.GeometryColumn!)}"
            : "NULL::geometry";

        return $"""
            SELECT s.fid AS fid, s.attrs AS attrs, s.geom AS geom, {GeomJson("s.geom", b)} AS geom_json,
                   s.actor AS actor, s.source_ref AS source_ref, s.correlation_id AS correlation_id, s.changed_at AS changed_at
            FROM (
                SELECT DISTINCT ON (h.{fid}) h.{fid} AS fid, {attrs} AS attrs, {geomInner} AS geom, h.{op} AS op,
                       {OptionalColumn("h", b.ActorColumn)} AS actor,
                       {OptionalColumn("h", b.SourceRefColumn)} AS source_ref,
                       {OptionalColumn("h", b.CorrelationIdColumn)} AS correlation_id,
                       h.{changed} AS changed_at
                FROM {b.QualifiedTable} h
                WHERE h.{changed} <= {atParam}
                ORDER BY h.{fid}, h.{changed} DESC
            ) s
            WHERE s.op IS DISTINCT FROM '{DeleteOperation}'
            """;
    }

    private static string AsOfSetTemporalTable(TemporalSourceBinding b, string atParam)
    {
        var pk = TemporalSourceBinding.Quote(b.FeatureIdColumn);
        var sys = TemporalSourceBinding.Quote(b.SystemPeriodColumn);
        var geomInner = b.GeometryEnabled
            ? $"t.{TemporalSourceBinding.Quote(b.GeometryColumn!)}"
            : "NULL::geometry";
        var minusGeom = b.GeometryEnabled ? $" - '{b.GeometryColumn}'" : string.Empty;

        return $"""
            SELECT t.{pk} AS fid,
                   (to_jsonb(t){minusGeom} - '{b.SystemPeriodColumn}') AS attrs,
                   {geomInner} AS geom,
                   {GeomJson(geomInner, b)} AS geom_json,
                   {OptionalColumn("t", b.ActorColumn)} AS actor,
                   {OptionalColumn("t", b.SourceRefColumn)} AS source_ref,
                   {OptionalColumn("t", b.CorrelationIdColumn)} AS correlation_id,
                   lower(t.{sys}) AS changed_at
            FROM {b.QualifiedTable} t
            WHERE lower(t.{sys}) <= {atParam}
              AND (upper(t.{sys}) IS NULL OR upper(t.{sys}) > {atParam})
            """;
    }

    private static string GeomJson(string geomExpr, TemporalSourceBinding b)
        => b.GeometryEnabled
            ? $"CASE WHEN {geomExpr} IS NOT NULL THEN ST_AsGeoJSON({geomExpr}) END"
            : "NULL::text";

    /// <summary>
    /// Probes the temporal source: whether the table exists and whether an index supports the as-of
    /// access pattern (leading <c>feature_id, changed_at</c> for audit logs, or the system-period
    /// column for temporal tables).
    /// </summary>
    public static string CapabilityProbe()
        => """
            WITH target AS (
                SELECT to_regclass(@reg) AS table_oid
            )
            SELECT
                (table_oid IS NOT NULL) AS table_exists,
                EXISTS (
                    SELECT 1
                    FROM pg_index idx
                    JOIN pg_attribute primary_att
                      ON primary_att.attrelid = idx.indrelid
                     AND primary_att.attnum = idx.indkey[0]
                    JOIN pg_attribute secondary_att
                      ON secondary_att.attrelid = idx.indrelid
                     AND secondary_att.attnum = idx.indkey[1]
                    WHERE idx.indrelid = table_oid
                      AND primary_att.attname = @primaryColumn
                      AND secondary_att.attname = @secondaryColumn
                ) AS asof_index
            FROM target
            """;

    /// <summary>Builds the deterministic, keyset-paged as-of snapshot page query.</summary>
    public static string AsOfPage(TemporalSourceBinding b)
        => $"""
            SELECT q.fid::text AS fid_text, q.attrs::text AS attrs_json, q.geom_json AS geom_json
            FROM ({AsOfSet(b, "@at")}) q
            WHERE q.fid::text > @cursor
            ORDER BY q.fid::text
            LIMIT @limit
            """;

    /// <summary>Builds the keyset-paged diff detail query between two as-of sets.</summary>
    public static string DiffDetail(TemporalSourceBinding b)
        => $"""
            WITH a AS ({AsOfSet(b, "@from")}), b AS ({AsOfSet(b, "@to")})
            SELECT COALESCE(a.fid, b.fid)::text AS fid_text,
                   (a.fid IS NULL) AS is_added,
                   (b.fid IS NULL) AS is_removed,
                   a.attrs::text AS from_attrs,
                   b.attrs::text AS to_attrs,
                   (a.geom IS DISTINCT FROM b.geom) AS geom_changed,
                   (a.attrs IS DISTINCT FROM b.attrs) AS attrs_changed,
                   a.actor AS from_actor, a.source_ref AS from_source, a.correlation_id AS from_corr, a.changed_at AS from_changed,
                   b.actor AS to_actor, b.source_ref AS to_source, b.correlation_id AS to_corr, b.changed_at AS to_changed
            FROM a FULL OUTER JOIN b ON a.fid = b.fid
            WHERE (a.fid IS NULL OR b.fid IS NULL OR a.attrs IS DISTINCT FROM b.attrs OR a.geom IS DISTINCT FROM b.geom)
              AND COALESCE(a.fid, b.fid)::text > @cursor
            ORDER BY COALESCE(a.fid, b.fid)::text
            LIMIT @limit
            """;

    /// <summary>Builds the diff summary count query over the full join.</summary>
    public static string DiffSummary(TemporalSourceBinding b)
        => $"""
            WITH a AS ({AsOfSet(b, "@from")}), b AS ({AsOfSet(b, "@to")})
            SELECT
                count(*) FILTER (WHERE a.fid IS NULL) AS added,
                count(*) FILTER (WHERE b.fid IS NULL) AS removed,
                count(*) FILTER (WHERE a.fid IS NOT NULL AND b.fid IS NOT NULL AND a.attrs IS DISTINCT FROM b.attrs) AS attr_changed,
                count(*) FILTER (WHERE a.fid IS NOT NULL AND b.fid IS NOT NULL AND a.attrs IS NOT DISTINCT FROM b.attrs AND a.geom IS DISTINCT FROM b.geom) AS geom_changed
            FROM a FULL OUTER JOIN b ON a.fid = b.fid
            """;

    /// <summary>Builds the per-feature timeline query (newest first, keyset by revision timestamp).</summary>
    public static string Timeline(TemporalSourceBinding b)
        => b.SourceKind == TemporalSourceKind.TemporalTable
            ? TimelineTemporalTable(b)
            : TimelineAuditLog(b);

    private static string TimelineAuditLog(TemporalSourceBinding b)
    {
        var fid = TemporalSourceBinding.Quote(b.FeatureIdColumn);
        var changed = TemporalSourceBinding.Quote(b.ChangedAtColumn);
        var op = TemporalSourceBinding.Quote(b.OperationColumn);
        var geomInner = b.GeometryEnabled ? $"h.{TemporalSourceBinding.Quote(b.GeometryColumn!)}" : "NULL::geometry";

        return $"""
            WITH revs AS (
                SELECT h.{changed} AS changed_at,
                       h.{op} AS op,
                       {OptionalColumn("h", b.ActorColumn)} AS actor,
                       {OptionalColumn("h", b.SourceRefColumn)} AS source_ref,
                       {OptionalColumn("h", b.CorrelationIdColumn)} AS correlation_id,
                       {OptionalJsonb("h", b.BeforeAttributesColumn)} AS before_attrs,
                       {OptionalJsonb("h", b.AfterAttributesColumn)} AS after_attrs,
                       {geomInner} AS geom
                FROM {b.QualifiedTable} h
                WHERE h.{fid}::text = @fid
            ),
            windowed AS (
                SELECT *, (geom IS DISTINCT FROM lag(geom) OVER (ORDER BY changed_at)) AS geom_changed
                FROM revs
            )
            SELECT changed_at, op, actor, source_ref, correlation_id,
                   before_attrs::text AS before_json, after_attrs::text AS after_json, geom_changed
            FROM windowed
            WHERE (@cursorTs::timestamptz IS NULL OR changed_at < @cursorTs)
            ORDER BY changed_at DESC
            LIMIT @limit
            """;
    }

    private static string TimelineTemporalTable(TemporalSourceBinding b)
    {
        var pk = TemporalSourceBinding.Quote(b.FeatureIdColumn);
        var sys = TemporalSourceBinding.Quote(b.SystemPeriodColumn);
        var geomInner = b.GeometryEnabled ? $"t.{TemporalSourceBinding.Quote(b.GeometryColumn!)}" : "NULL::geometry";
        var minusGeom = b.GeometryEnabled ? $" - '{b.GeometryColumn}'" : string.Empty;

        return $"""
            WITH revs AS (
                SELECT lower(t.{sys}) AS changed_at,
                       {OptionalColumn("t", b.ActorColumn)} AS actor,
                       {OptionalColumn("t", b.SourceRefColumn)} AS source_ref,
                       {OptionalColumn("t", b.CorrelationIdColumn)} AS correlation_id,
                       (to_jsonb(t){minusGeom} - '{b.SystemPeriodColumn}') AS after_attrs,
                       {geomInner} AS geom
                FROM {b.QualifiedTable} t
                WHERE t.{pk}::text = @fid
            ),
            windowed AS (
                SELECT *,
                       lag(after_attrs) OVER (ORDER BY changed_at) AS before_attrs,
                       (geom IS DISTINCT FROM lag(geom) OVER (ORDER BY changed_at)) AS geom_changed,
                       (row_number() OVER (ORDER BY changed_at) = 1) AS is_first
                FROM revs
            )
            SELECT changed_at,
                   CASE WHEN is_first THEN 'INSERT' ELSE 'UPDATE' END AS op,
                   actor, source_ref, correlation_id,
                   before_attrs::text AS before_json, after_attrs::text AS after_json, geom_changed
            FROM windowed
            WHERE (@cursorTs::timestamptz IS NULL OR changed_at < @cursorTs)
            ORDER BY changed_at DESC
            LIMIT @limit
            """;
    }

    /// <summary>Builds the checkpoint enumeration query (distinct revision markers, newest first).</summary>
    public static string Checkpoints(TemporalSourceBinding b)
    {
        if (b.SourceKind == TemporalSourceKind.TemporalTable)
        {
            var sys = TemporalSourceBinding.Quote(b.SystemPeriodColumn);
            return $"""
                SELECT DISTINCT lower(t.{sys}) AS ts, NULL::text AS corr, NULL::text AS src
                FROM {b.QualifiedTable} t
                WHERE lower(t.{sys}) IS NOT NULL
                ORDER BY ts DESC
                LIMIT @limit
                """;
        }

        var changed = TemporalSourceBinding.Quote(b.ChangedAtColumn);
        return $"""
            SELECT DISTINCT ON (h.{changed}) h.{changed} AS ts,
                   {OptionalColumn("h", b.CorrelationIdColumn)} AS corr,
                   {OptionalColumn("h", b.SourceRefColumn)} AS src
            FROM {b.QualifiedTable} h
            ORDER BY h.{changed} DESC
            LIMIT @limit
            """;
    }

    /// <summary>Builds the query that resolves a named/job/release/edit-session reference to a UTC instant.</summary>
    public static string ResolveReference(TemporalSourceBinding b)
    {
        if (b.SourceKind == TemporalSourceKind.TemporalTable)
        {
            // Temporal tables expose only timestamp cursors; named references are unresolvable.
            return "SELECT NULL::timestamptz";
        }

        var changed = TemporalSourceBinding.Quote(b.ChangedAtColumn);
        var corrMatch = b.CorrelationIdColumn is null
            ? "FALSE"
            : $"h.{TemporalSourceBinding.Quote(b.CorrelationIdColumn)} = @ref";
        var srcMatch = b.SourceRefColumn is null
            ? "FALSE"
            : $"h.{TemporalSourceBinding.Quote(b.SourceRefColumn)} = @ref";

        return $"""
            SELECT max(h.{changed})
            FROM {b.QualifiedTable} h
            WHERE {corrMatch} OR {srcMatch}
            """;
    }

    /// <summary>
    /// Builds the append-only corrective rollback insert that restores the layer to the state at
    /// <c>@to</c> by appending INSERT/UPDATE/DELETE revisions relative to the current state at
    /// <c>@now</c>. No existing rows are modified or deleted. Returns the affected row count.
    /// Requires an audit-log source with an after-attributes column.
    /// </summary>
    public static string RollbackCorrectiveInsert(TemporalSourceBinding b)
    {
        var columns = new List<string>
        {
            TemporalSourceBinding.Quote(b.FeatureIdColumn),
            TemporalSourceBinding.Quote(b.OperationColumn),
            TemporalSourceBinding.Quote(b.ChangedAtColumn)
        };
        var values = new List<string>
        {
            "COALESCE(d.fid, c.fid)",
            "CASE WHEN c.fid IS NULL THEN 'INSERT' WHEN d.fid IS NULL THEN 'DELETE' ELSE 'UPDATE' END",
            "@now"
        };

        if (b.ActorColumn is not null)
        {
            columns.Add(TemporalSourceBinding.Quote(b.ActorColumn));
            values.Add("@actor");
        }

        if (b.SourceRefColumn is not null)
        {
            columns.Add(TemporalSourceBinding.Quote(b.SourceRefColumn));
            values.Add("@job");
        }

        if (b.CorrelationIdColumn is not null)
        {
            columns.Add(TemporalSourceBinding.Quote(b.CorrelationIdColumn));
            values.Add("@corr");
        }

        // After-attributes (required for rollback): desired state, null for corrective deletes.
        columns.Add(TemporalSourceBinding.Quote(b.AfterAttributesColumn!));
        values.Add("CASE WHEN d.fid IS NULL THEN NULL ELSE d.attrs END");

        if (b.BeforeAttributesColumn is not null)
        {
            columns.Add(TemporalSourceBinding.Quote(b.BeforeAttributesColumn));
            values.Add("CASE WHEN c.fid IS NULL THEN NULL ELSE c.attrs END");
        }

        if (b.GeometryEnabled)
        {
            columns.Add(TemporalSourceBinding.Quote(b.GeometryColumn!));
            values.Add("CASE WHEN d.fid IS NULL THEN NULL ELSE d.geom END");
        }

        var columnList = string.Join(", ", columns);
        var valueList = string.Join(", ", values);

        return $"""
            INSERT INTO {b.QualifiedTable} ({columnList})
            SELECT {valueList}
            FROM ({AsOfSet(b, "@to")}) d
            FULL OUTER JOIN ({AsOfSet(b, "@now")}) c ON d.fid = c.fid
            WHERE d.fid IS NULL OR c.fid IS NULL OR d.attrs IS DISTINCT FROM c.attrs OR d.geom IS DISTINCT FROM c.geom
            """;
    }

    private static string OptionalColumn(string alias, string? column)
        => column is null ? "NULL::text" : $"{alias}.{TemporalSourceBinding.Quote(column)}";

    private static string OptionalJsonb(string alias, string? column)
        => column is null ? "NULL::jsonb" : $"{alias}.{TemporalSourceBinding.Quote(column)}";
}
