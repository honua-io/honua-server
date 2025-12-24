// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Queries.Filters;

/// <summary>
/// Represents a SQL fragment with parameterized values
/// </summary>
/// <param name="Sql">The SQL string with parameter placeholders</param>
/// <param name="Parameters">Parameter values in order</param>
public record SqlFragment(string Sql, IReadOnlyList<object?> Parameters);
