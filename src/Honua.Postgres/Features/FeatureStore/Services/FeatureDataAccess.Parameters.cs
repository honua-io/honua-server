// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Npgsql;

namespace Honua.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureDataAccess
{
    private void AddQueryParameters(NpgsqlCommand command, FeatureQuery query, int layerId, List<object> whereParameters)
    {
        var parameterIndex = 0;
        AddParameterValue(command, ref parameterIndex, layerId);

        if (query.SpatialFilter.HasValue &&
            query.SpatialFilter.Value.SpatialRelationship == SpatialRelationship.NearestNeighbor &&
            query.SpatialFilter.Value.ReturnDistance)
        {
            var filter = query.SpatialFilter.Value;

            // Distance geometry is used in SELECT before WHERE, so add it first.
            AddParameterValue(command, ref parameterIndex, filter.Geometry);

            AddWhereParameters(command, whereParameters, ref parameterIndex);

            AddKnnParameters(command, filter, query, ref parameterIndex, includeDistanceGeometry: false);

            if (query.Offset.HasValue)
            {
                AddParameterValue(command, ref parameterIndex, query.Offset.Value);
            }

            return;
        }

        AddWhereParameters(command, whereParameters, ref parameterIndex);

        if (query.SpatialFilter.HasValue)
        {
            AddSpatialFilterParameters(command, query, ref parameterIndex);
        }
        else
        {
            AddRegularPaginationParameters(command, query, ref parameterIndex);
        }

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

    private void AddSpatialFilterParameters(NpgsqlCommand command, FeatureQuery query, ref int parameterIndex)
    {
        var filter = query.SpatialFilter!.Value;

        if (filter.SpatialRelationship == SpatialRelationship.NearestNeighbor)
        {
            AddKnnParameters(command, filter, query, ref parameterIndex);
        }
        else
        {
            AddRegularSpatialParameters(command, filter, query, ref parameterIndex);
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

    private void AddRegularSpatialParameters(NpgsqlCommand command, SpatialFilter filter, FeatureQuery query, ref int parameterIndex)
    {
        AddParameterValue(command, ref parameterIndex, filter.Geometry);

        if (filter.SpatialRelationship == SpatialRelationship.WithinDistance ||
            filter.SpatialRelationship == SpatialRelationship.BeyondDistance)
        {
            var distanceInMeters = _geometryProcessor.ConvertDistanceToMeters(filter.Distance ?? 0, filter.DistanceUnit);
            AddParameterValue(command, ref parameterIndex, distanceInMeters);
        }

        if (query.Limit.HasValue)
        {
            AddParameterValue(command, ref parameterIndex, query.Limit.Value);
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
        var parameterValue = value ?? DBNull.Value;

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
}
