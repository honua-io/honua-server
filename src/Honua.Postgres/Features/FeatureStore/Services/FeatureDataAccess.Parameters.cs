// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Npgsql;

namespace Honua.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureDataAccess
{
    private static void AddQueryParameters(NpgsqlCommand command, FeatureQuery query, int layerId, List<object> whereParameters)
    {
        var parameterIndex = 0;
        AddParameterValue(command, ref parameterIndex, layerId);

        var isKnnQuery = query.SpatialFilter.HasValue &&
                         query.SpatialFilter.Value.SpatialRelationship == SpatialRelationship.NearestNeighbor;

        if (isKnnQuery)
        {
            var filter = query.SpatialFilter!.Value;

            if (filter.ReturnDistance)
            {
                // Distance geometry is used in SELECT before WHERE, so add it first.
                AddParameterValue(command, ref parameterIndex, filter.Geometry);
            }

            AddWhereParameters(command, whereParameters, ref parameterIndex);

            AddKnnParameters(command, filter, query, ref parameterIndex, includeDistanceGeometry: false);

            if (query.Offset.HasValue)
            {
                AddParameterValue(command, ref parameterIndex, query.Offset.Value);
            }

            return;
        }

        // Non-KNN `returnDistance` queries project a runtime distance column in the SELECT
        // clause whose geometry parameter is appended to the WHERE-parameter list by the
        // select-clause builder itself (see FeatureQueryBuilder select clauses). It is bound
        // here as part of AddWhereParameters in positional order, so no separate add is
        // needed — and count/objectIds/optimized builders that omit the distance column also
        // omit the parameter, keeping the binding self-consistent across query shapes.
        AddWhereParameters(command, whereParameters, ref parameterIndex);

        AddRegularPaginationParameters(command, query, ref parameterIndex);

        if (query.Offset.HasValue)
        {
            AddParameterValue(command, ref parameterIndex, query.Offset.Value);
        }
    }

    private static void AddWhereParameters(NpgsqlCommand command, List<object> whereParameters, ref int parameterIndex)
    {
        foreach (var param in whereParameters)
        {
            AddParameterValue(command, ref parameterIndex, param);
        }
    }



    private static void AddKnnParameters(
        NpgsqlCommand command,
        SpatialFilter filter,
        FeatureQuery query,
        ref int parameterIndex,
        bool includeDistanceGeometry = true)
    {
        if (includeDistanceGeometry && filter.ReturnDistance)
        {
            AddParameterValue(command, ref parameterIndex, filter.Geometry);
        }

        AddParameterValue(command, ref parameterIndex, filter.Geometry);

        var limit = filter.NearestCount ?? query.Limit;
        if (limit.HasValue)
        {
            AddParameterValue(command, ref parameterIndex, limit.Value);
        }
    }




    private static void AddRegularPaginationParameters(NpgsqlCommand command, FeatureQuery query, ref int parameterIndex)
    {
        if (query.Limit.HasValue)
        {
            AddParameterValue(command, ref parameterIndex, query.Limit.Value);
        }
    }

    private static void AddParameterValue(NpgsqlCommand command, ref int parameterIndex, object? value)
    {
        var parameterValue = NormalizeParameterValue(value);

        if (command.Parameters.Count > parameterIndex)
        {
            command.Parameters[parameterIndex].Value = parameterValue;
        }
        else
        {
            command.Parameters.AddWithValue(parameterValue);
        }

        parameterIndex++;
    }

    private static object NormalizeParameterValue(object? value)
    {
        return value switch
        {
            null => DBNull.Value,
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime(),
            // DateTimeOffset.UtcDateTime (used by temporal filters) yields a Kind=Unspecified
            // DateTime, which Npgsql refuses to bind to a timestamptz column -> HTTP 500 on
            // datetime-filtered tiles/extents. Force a UTC kind.
            DateTime dateTime => DateTime.SpecifyKind(
                dateTime.Kind == DateTimeKind.Local ? dateTime.ToUniversalTime() : dateTime,
                DateTimeKind.Utc),
            _ => value
        };
    }
}
