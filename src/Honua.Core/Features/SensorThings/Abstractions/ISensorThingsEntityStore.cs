// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Features.SensorThings.Abstractions;

public interface ISensorThingsEntityStore
{
    Honua.Core.Queries.Filters.SqlFragment TranslateFilter(string entitySet, Honua.Core.Queries.Filters.FilterExpression expression);
    Honua.Core.Queries.Filters.SqlFragment TranslateOrderExpression(string entitySet, Honua.Core.Queries.Filters.FilterExpression expression);
    Task<JsonElement?> GetEntityAsync(string entitySet, long id, CancellationToken cancellationToken);

    Task<SensorThingsEntityPage> QueryEntitiesAsync(string entitySet, SensorThingsEntityQuery query, CancellationToken cancellationToken);

    Task<long> CreateEntityAsync(string entitySet, JsonElement body, CancellationToken cancellationToken);

    Task<IReadOnlyList<long>> CreateEntityBatchAsync(string entitySet, IReadOnlyList<JsonElement> bodies, CancellationToken cancellationToken);

    Task<bool> PatchEntityAsync(string entitySet, long id, JsonElement body, CancellationToken cancellationToken);

    Task<bool> DeleteEntityAsync(string entitySet, long id, CancellationToken cancellationToken);

    Task<long> CountUnresolvedFeaturesAsync(CancellationToken cancellationToken);
}

public sealed record SensorThingsEntityQuery(string? WhereSql, IReadOnlyList<object?> Parameters,
    string OrderBySql, int Skip, int Top, string? ParentSet = null, long? ParentId = null, string? Navigation = null, bool Count = false,
    bool IncludeDerivedDatastreamIntervals = true);

public sealed record SensorThingsEntityPage(IReadOnlyList<JsonElement> Entities, long Count);

public sealed class SensorThingsValidationException : Exception
{
    public SensorThingsValidationException(string message) : base(message) { }
}

public sealed class SensorThingsConflictException : Exception
{
    public SensorThingsConflictException(string message) : base(message) { }
}
