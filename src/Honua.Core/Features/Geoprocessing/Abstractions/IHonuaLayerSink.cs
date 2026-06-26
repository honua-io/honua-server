// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Abstractions;

/// <summary>
/// How a <see cref="IHonuaLayerSink"/> reconciles an incoming feature batch against the
/// rows already in the destination table. Mirrors the import-path load modes (#2209).
/// </summary>
public enum HonuaLayerLoadMode
{
    /// <summary>Insert the incoming rows alongside whatever is already present.</summary>
    Append,

    /// <summary>Remove every existing row in the destination table, then insert the incoming rows.</summary>
    Replace,

    /// <summary>
    /// Delete existing rows whose key-field attribute values match an incoming row, then
    /// insert the incoming rows — an idempotent reload keyed on
    /// <see cref="HonuaLayerSinkRequest.KeyFields"/>.
    /// </summary>
    Upsert,
}

/// <summary>
/// One feature to load: its geometry encoded as well-known binary plus a JSON object of
/// attributes. The attributes JSON already carries the reserved
/// <c>__pipeline_batch_id</c> key the rollback pattern relies on, so the sink stays free
/// of geometry-library and feature-model dependencies.
/// </summary>
/// <param name="WellKnownBinary">The feature geometry encoded as WKB.</param>
/// <param name="AttributesJson">The feature attributes as a JSON object string (including the batch-id key).</param>
public sealed record HonuaLayerSinkRow(byte[] WellKnownBinary, string AttributesJson);

/// <summary>
/// The destination description and load policy for a <see cref="IHonuaLayerSink"/> load.
/// </summary>
/// <param name="Schema">Destination schema (validated identifier).</param>
/// <param name="Table">Destination table / layer name (validated identifier).</param>
/// <param name="GeometryColumn">Destination geometry column (validated identifier).</param>
/// <param name="TargetSrid">SRID applied to the destination geometry column.</param>
/// <param name="LoadMode">How the incoming rows reconcile with existing rows.</param>
/// <param name="BatchId">The run batch id tagged on every row for soft-delete rollback.</param>
/// <param name="KeyFields">
/// Attribute names that identify a row for <see cref="HonuaLayerLoadMode.Upsert"/>;
/// ignored for the other modes.
/// </param>
public sealed record HonuaLayerSinkRequest(
    string Schema,
    string Table,
    string GeometryColumn,
    int TargetSrid,
    HonuaLayerLoadMode LoadMode,
    string BatchId,
    IReadOnlyList<string> KeyFields);

/// <summary>
/// The result of a <see cref="IHonuaLayerSink"/> load.
/// </summary>
/// <param name="FeaturesWritten">The number of rows inserted into the destination table.</param>
/// <param name="Schema">The destination schema that was written.</param>
/// <param name="Table">The destination table that was written.</param>
/// <param name="BatchId">The batch id tagged on the written rows (for rollback).</param>
public sealed record HonuaLayerSinkOutcome(
    long FeaturesWritten,
    string Schema,
    string Table,
    string BatchId);

/// <summary>
/// Optional capability that loads an upstream feature artifact into a named layer in the
/// Honua <em>catalog</em> database. It is the deliberate seam that lets the
/// <c>sink.honua-layer</c> DAG node reach the catalog's own database without coupling the
/// geoprocessing dispatcher — which is constructed unconditionally, including in lean,
/// Postgres-free deployments — to the catalog <c>NpgsqlDataSource</c>.
/// </summary>
/// <remarks>
/// <para>
/// The implementation lives with the catalog provider (it depends on the catalog data
/// source) and is registered <em>only</em> when that database is present. In a lean
/// deployment the capability is simply absent: the <c>sink.honua-layer</c> executor
/// resolves it as <see langword="null"/> and fails the node closed with a clear
/// "unavailable in this deployment" message instead of forcing a Postgres dependency on
/// the dispatcher or leaking a catalog connection string through plan parameters.
/// </para>
/// <para>
/// Rows are pre-encoded (WKB + attributes JSON) by the executor so this abstraction never
/// references a geometry or feature-model type; the attributes JSON carries the reserved
/// <c>__pipeline_batch_id</c> key so a completed load can be rolled back by batch id.
/// </para>
/// </remarks>
public interface IHonuaLayerSink
{
    /// <summary>
    /// Loads the supplied rows into the destination described by <paramref name="request"/>
    /// using its load mode, within a single transaction so a failure leaves the catalog
    /// table unchanged.
    /// </summary>
    /// <param name="request">The destination and load policy.</param>
    /// <param name="rows">The pre-encoded feature rows to load.</param>
    /// <param name="cancellationToken">Token used to abort the load.</param>
    /// <returns>The load outcome (rows written, destination, batch id).</returns>
    Task<HonuaLayerSinkOutcome> LoadAsync(
        HonuaLayerSinkRequest request,
        IReadOnlyList<HonuaLayerSinkRow> rows,
        CancellationToken cancellationToken);
}
