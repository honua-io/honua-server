// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Queries.Filters;

/// <summary>
/// Per-provider SQL dialect surface used by query builders, filter translators and DDL
/// generators to keep identifier-quoting and parameter syntax consistent across one provider.
/// </summary>
/// <remarks>
/// <para>
/// Centralising identifier quoting in <see cref="ISqlDialect"/> closes a per-provider
/// SQL-injection boundary: every provider previously reimplemented its own
/// <c>QuoteIdentifier</c> (Postgres double-quotes, MySQL backticks, SQL Server brackets,
/// DuckDB double-quotes). Subtle differences (validation regex, escaping rules,
/// empty-string handling) drifted between callers; the dialect type is the single
/// place each provider's quoting rules are defined and the only thing a caller needs
/// to reach for.
/// </para>
/// <para>
/// Parameterised queries should still be preferred everywhere user-supplied values
/// flow into SQL. <see cref="QuoteLiteral(string)"/> exists for the narrow set of
/// callers — DDL emission, schema names embedded in DDL, dynamic SQL strings — where
/// the parameter binder is not in play.
/// </para>
/// </remarks>
public interface ISqlDialect
{
    /// <summary>
    /// Short, stable identifier for the dialect (<c>"postgres"</c>, <c>"mysql"</c>,
    /// <c>"sqlserver"</c>, <c>"duckdb"</c>). Used by diagnostics and the architecture
    /// guardrail that asserts each provider exports exactly one dialect.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Parameter-placeholder prefix the provider's ADO.NET driver expects.
    /// </summary>
    /// <remarks>
    /// Npgsql, MySqlConnector and Microsoft.Data.SqlClient all accept <c>@</c>-prefixed
    /// named parameters. DuckDB.NET binds <c>$</c>-prefixed positional parameters.
    /// </remarks>
    string ParameterPrefix { get; }

    /// <summary>
    /// Quotes an identifier (schema / table / column / view name) according to the
    /// provider's identifier rules.
    /// </summary>
    /// <remarks>
    /// Implementations MUST:
    /// <list type="bullet">
    ///   <item>reject null, empty or whitespace input with <see cref="ArgumentException"/>;</item>
    ///   <item>double any embedded quote character so the result is unambiguous;</item>
    ///   <item>return the quoted form using the provider's canonical delimiter
    ///         (double-quote for ANSI/Postgres/DuckDB, backtick for MySQL, square brackets
    ///         for SQL Server).</item>
    /// </list>
    /// Implementations MAY apply a stricter allow-list (Postgres and DuckDB permit any
    /// character once doubled; MySQL and SQL Server clamp to a simple-identifier regex
    /// because the values come from user-supplied field metadata and the simpler form
    /// is unambiguous and resistant to encoding tricks).
    /// </remarks>
    string QuoteIdentifier(string identifier);

    /// <summary>
    /// Escapes and single-quotes a string literal for inline embedding in SQL.
    /// </summary>
    /// <remarks>
    /// Prefer parameterised queries; reach for <see cref="QuoteLiteral(string)"/> only
    /// when the call site genuinely cannot bind a parameter (DDL, schema names embedded
    /// in <c>CREATE INDEX</c>, system-catalog probes). Implementations MUST double single
    /// quotes; they MAY also reject backslash-escape modes the provider supports but the
    /// project does not enable.
    /// </remarks>
    string QuoteLiteral(string literal);
}
