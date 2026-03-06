// Copyright (c) 2026 Honua Project Contributors
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using Geospatial.V1;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Transport.Converters;

/// <summary>
/// Converter for statistic definitions between domain models and gRPC messages.
/// </summary>
public static class StatisticDefinitionConverter
{
    /// <summary>
    /// Converts a domain StatisticDefinition to a gRPC StatisticDefinition message.
    /// </summary>
    /// <param name="domainStat">The domain statistic definition</param>
    /// <returns>gRPC statistic definition message</returns>
    public static Geospatial.V1.StatisticDefinition ToGrpc(Features.FeatureStore.Domain.StatisticDefinition domainStat)
    {
        var grpcStat = new Geospatial.V1.StatisticDefinition
        {
            OnStatisticField = domainStat.OnStatisticField,
            StatisticType = ConvertStatisticType(domainStat.StatisticType)
        };

        if (!string.IsNullOrEmpty(domainStat.OutStatisticFieldName))
        {
            grpcStat.OutStatisticFieldName = domainStat.OutStatisticFieldName;
        }

        return grpcStat;
    }

    /// <summary>
    /// Converts a gRPC StatisticDefinition message to a domain StatisticDefinition.
    /// </summary>
    /// <param name="grpcStat">The gRPC statistic definition message</param>
    /// <returns>Domain statistic definition</returns>
    public static Features.FeatureStore.Domain.StatisticDefinition FromGrpc(Geospatial.V1.StatisticDefinition grpcStat)
    {
        return new Features.FeatureStore.Domain.StatisticDefinition
        {
            OnStatisticField = grpcStat.OnStatisticField,
            StatisticType = ConvertStatisticType(grpcStat.StatisticType),
            OutStatisticFieldName = grpcStat.OutStatisticFieldName ?? string.Empty
        };
    }

    private static Geospatial.V1.StatisticType ConvertStatisticType(Features.FeatureStore.Domain.StatisticType domainType)
    {
        return domainType switch
        {
            Features.FeatureStore.Domain.StatisticType.Count => Geospatial.V1.StatisticType.Count,
            Features.FeatureStore.Domain.StatisticType.Sum => Geospatial.V1.StatisticType.Sum,
            Features.FeatureStore.Domain.StatisticType.Min => Geospatial.V1.StatisticType.Min,
            Features.FeatureStore.Domain.StatisticType.Max => Geospatial.V1.StatisticType.Max,
            Features.FeatureStore.Domain.StatisticType.Avg => Geospatial.V1.StatisticType.Avg,
            Features.FeatureStore.Domain.StatisticType.Stddev => Geospatial.V1.StatisticType.Stddev,
            Features.FeatureStore.Domain.StatisticType.Var => Geospatial.V1.StatisticType.Var,
            _ => Geospatial.V1.StatisticType.Unspecified
        };
    }

    private static Features.FeatureStore.Domain.StatisticType ConvertStatisticType(Geospatial.V1.StatisticType grpcType)
    {
        return grpcType switch
        {
            Geospatial.V1.StatisticType.Count => Features.FeatureStore.Domain.StatisticType.Count,
            Geospatial.V1.StatisticType.Sum => Features.FeatureStore.Domain.StatisticType.Sum,
            Geospatial.V1.StatisticType.Min => Features.FeatureStore.Domain.StatisticType.Min,
            Geospatial.V1.StatisticType.Max => Features.FeatureStore.Domain.StatisticType.Max,
            Geospatial.V1.StatisticType.Avg => Features.FeatureStore.Domain.StatisticType.Avg,
            Geospatial.V1.StatisticType.Stddev => Features.FeatureStore.Domain.StatisticType.Stddev,
            Geospatial.V1.StatisticType.Var => Features.FeatureStore.Domain.StatisticType.Var,
            _ => Features.FeatureStore.Domain.StatisticType.Count // Default fallback
        };
    }
}