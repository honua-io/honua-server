// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Holds a parameterized SQL query with its parameters
/// </summary>
/// <param name="Sql">The SQL query string with parameter placeholders</param>
/// <param name="WhereParameters">The parameter values for the query</param>
public record ParameterizedQuery(string Sql, List<object> WhereParameters);
