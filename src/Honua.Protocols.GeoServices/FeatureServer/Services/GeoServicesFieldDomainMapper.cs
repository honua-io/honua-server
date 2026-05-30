// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer.Services;

internal static class GeoServicesFieldDomainMapper
{
    public static GeoServicesFieldDomainInfo? Map(MetadataV2FieldDomain? domain)
    {
        if (domain is null)
        {
            return null;
        }

        return new GeoServicesFieldDomainInfo
        {
            Type = domain.Type,
            Name = domain.Name,
            CodedValues = domain.CodedValues.Count == 0
                ? null
                : domain.CodedValues
                    .Select(value => new GeoServicesFieldDomainCodedValueInfo
                    {
                        Code = value.Code.Clone(),
                        Name = value.Name,
                    })
                    .ToArray(),
            Range = domain.Range is null
                ? null
                : domain.Range.Select(value => value.Clone()).ToArray(),
            MergePolicy = domain.MergePolicy,
            SplitPolicy = domain.SplitPolicy,
        };
    }
}
