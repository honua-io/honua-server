// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Databricks.Features.FeatureStore.Services;

/// <summary>
/// A SQL statement destined for the Databricks SQL Statement Execution API, together
/// with the named parameters that bind into its <c>:name</c> markers.
/// </summary>
/// <param name="Sql">SQL text with <c>:name</c> parameter markers.</param>
/// <param name="Parameters">Named parameter values, in declaration order.</param>
internal sealed record DatabricksSqlStatement(string Sql, IReadOnlyList<DatabricksSqlParameter> Parameters)
{
    /// <summary>A statement with no bound parameters.</summary>
    public static DatabricksSqlStatement WithoutParameters(string sql) => new(sql, []);
}

/// <summary>
/// A single named parameter bound into a <see cref="DatabricksSqlStatement"/>.
/// </summary>
/// <param name="Name">Parameter name without the leading colon.</param>
/// <param name="Value">String-rendered value, or <see langword="null"/> for SQL NULL.</param>
/// <param name="Type">
/// Optional Databricks SQL type hint (for example <c>BIGINT</c>, <c>DOUBLE</c>).
/// Null lets the warehouse infer the type from the literal.
/// </param>
internal sealed record DatabricksSqlParameter(string Name, string? Value, string? Type = null);
