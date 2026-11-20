// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Grpc.Core;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer;

namespace Honua.Server.Features.Protocols.Grpc;

internal sealed partial class HonuaFeatureService
{
    // Sort keys resolve against the fields this caller may see, as FeatureServer orderByFields does:
    // unknown, hidden and masked names are refused, and the declared field type travels with the
    // clause because an untyped managed JSONB attribute sorts a numeric value as text.
    private static ImmutableArray<OrderByClause>? ResolveCallerOrderBy(
        string? orderBy,
        MetadataV2Object resource,
        IReadOnlySet<string> maskedFields)
    {
        try
        {
            return FeatureServerOrderByFields.ParseCallerOrderBy(orderBy, resource.WithSchemaFieldsHidden(maskedFields));
        }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }
}
