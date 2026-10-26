// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Holds a parameterized SQL query with its parameters
/// </summary>
/// <param name="Sql">The SQL query string with parameter placeholders</param>
/// <param name="WhereParameters">The parameter values for the query</param>
public record ParameterizedQuery(string Sql, List<object> WhereParameters)
{
    /// <summary>
    /// Asks a provider that compiles plans (PostgreSQL LLVM JIT) to run this statement
    /// without JIT. Set by builders whose SQL is a large CTE chain that spends its time
    /// in extension functions such as PostGIS, where compilation costs seconds and
    /// saves nothing. Providers without JIT ignore it.
    /// </summary>
    public bool DisableJit { get; init; }
}
