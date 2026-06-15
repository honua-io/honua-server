// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Abstractions;

/// <summary>
/// Read-only view over the current row of a streaming query result, used by the
/// row-mapper overloads on <see cref="IDatabaseSession"/>. Exposes ordinal-based
/// field access without leaking <c>System.Data.Common.DbDataReader</c> into the
/// abstractions surface (audit C3 / ADR 0046).
/// </summary>
/// <remarks>
/// Instances are only valid while the mapper callback executes for the current
/// row. Implementations may reuse a single instance across rows, so mappers
/// MUST NOT capture the row for later use — materialise values inside the
/// callback and return an owned result object.
/// </remarks>
public interface IDatabaseRow
{
    /// <summary>
    /// Gets the number of columns in the current row.
    /// </summary>
    int FieldCount { get; }

    /// <summary>
    /// Determines whether the column at <paramref name="ordinal"/> contains a database null.
    /// </summary>
    /// <param name="ordinal">Zero-based column ordinal.</param>
    /// <returns><c>true</c> when the column value is <c>NULL</c>.</returns>
    bool IsNull(int ordinal);

    /// <summary>
    /// Gets the value of the column at <paramref name="ordinal"/> as <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The expected field type (e.g. <c>long</c>, <c>string</c>, <c>double</c>).</typeparam>
    /// <param name="ordinal">Zero-based column ordinal.</param>
    /// <returns>The column value.</returns>
    T GetFieldValue<T>(int ordinal);

    /// <summary>
    /// Resolves a column name to its zero-based ordinal.
    /// </summary>
    /// <param name="name">The column name.</param>
    /// <returns>The zero-based column ordinal.</returns>
    int GetOrdinal(string name);
}
