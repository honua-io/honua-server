// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Queries.Filters;

/// <summary>
/// Represents a SQL fragment with parameterized values
/// </summary>
/// <param name="Sql">The SQL string with parameter placeholders</param>
/// <param name="Parameters">Parameter values in order</param>
public record SqlFragment(string Sql, IReadOnlyList<object?> Parameters);
