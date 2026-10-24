// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Holds a parameterized SQL query with its parameters
/// </summary>
/// <param name="Sql">The SQL query string with parameter placeholders</param>
/// <param name="WhereParameters">The parameter values for the query</param>
public record ParameterizedQuery(string Sql, List<object> WhereParameters);
