// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;

namespace Honua.Core.Features.Authorization.Abstractions;

public interface ICombinedReadSecuritySource
{
    bool CanResolveWith(IFieldMaskSource fieldSource);

    Task<ReadSecurityResolution> ResolveCombinedAsync(
        MetadataV2Object resource,
        IFieldMaskSource fieldSource,
        CancellationToken cancellationToken = default);
}

public readonly record struct ReadSecurityResolution(SqlFragment? RowFilter, ImmutableArray<string> MaskedFields);
