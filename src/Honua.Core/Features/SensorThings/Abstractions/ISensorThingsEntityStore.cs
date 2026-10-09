// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Features.SensorThings.Abstractions;

/// <summary>Transactional catalog and observation access for the eight sensing entities.</summary>
public interface ISensorThingsEntityStore
{
    /// <summary>Translates a canonical filter with the shared provider SQL visitor.</summary>
    Honua.Core.Queries.Filters.SqlFragment TranslateFilter(string entitySet, Honua.Core.Queries.Filters.FilterExpression expression);
    /// <summary>Translates a primitive ordering expression with canonical JSON scalar types.</summary>
    Honua.Core.Queries.Filters.SqlFragment TranslateOrderExpression(string entitySet, Honua.Core.Queries.Filters.FilterExpression expression);
    /// <summary>Reads one entity, including its canonical data properties.</summary>
    Task<JsonElement?> GetEntityAsync(string entitySet, long id, CancellationToken cancellationToken);

    /// <summary>Queries an entity set using validated, parameterized predicates and ordering.</summary>
    Task<SensorThingsEntityPage> QueryEntitiesAsync(string entitySet, SensorThingsEntityQuery query, CancellationToken cancellationToken);

    /// <summary>Creates an entity and its supplied related entities in one transaction.</summary>
    Task<long> CreateEntityAsync(string entitySet, JsonElement body, CancellationToken cancellationToken);

    /// <summary>Creates a bounded entity batch atomically, preserving input order.</summary>
    Task<IReadOnlyList<long>> CreateEntityBatchAsync(string entitySet, IReadOnlyList<JsonElement> bodies, CancellationToken cancellationToken);

    /// <summary>Updates supplied properties and relationships atomically.</summary>
    Task<bool> PatchEntityAsync(string entitySet, long id, JsonElement body, CancellationToken cancellationToken);

    /// <summary>Deletes an entity and the dependent entities required by the sensing model.</summary>
    Task<bool> DeleteEntityAsync(string entitySet, long id, CancellationToken cancellationToken);

    /// <summary>Counts legacy observations whose feature of interest requires reconciliation.</summary>
    Task<long> CountUnresolvedFeaturesAsync(CancellationToken cancellationToken);
}

/// <summary>A validated entity query, optionally scoped by a parent relationship.</summary>
/// <param name="WhereSql">Parameterized filter over provider columns.</param>
/// <param name="Parameters">Filter values.</param>
/// <param name="OrderBySql">Validated order clause.</param>
/// <param name="Skip">Rows to skip.</param>
/// <param name="Top">Rows to fetch.</param>
/// <param name="ParentSet">Parent entity set for a navigation query.</param>
/// <param name="ParentId">Parent identifier.</param>
/// <param name="Navigation">Navigation property.</param>
public sealed record SensorThingsEntityQuery(string? WhereSql, IReadOnlyList<object?> Parameters,
    string OrderBySql, int Skip, int Top, string? ParentSet = null, long? ParentId = null, string? Navigation = null);

/// <summary>Entities and the total matching count before paging.</summary>
/// <param name="Entities">The returned page.</param>
/// <param name="Count">Total matching entities.</param>
public sealed record SensorThingsEntityPage(IReadOnlyList<JsonElement> Entities, long Count);

/// <summary>A rejected sensing mutation; its transaction is rolled back.</summary>
public sealed class SensorThingsValidationException : Exception
{
    /// <summary>Creates a validation failure with a client-readable message.</summary>
    public SensorThingsValidationException(string message) : base(message) { }
}
