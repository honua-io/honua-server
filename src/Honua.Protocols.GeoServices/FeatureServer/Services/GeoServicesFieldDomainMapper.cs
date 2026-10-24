// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

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
