// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;

namespace Honua.Core.Features.Authorization.Abstractions;

public interface IRowLevelSecurityFilterSource
{
    Task<SqlFragment?> ResolveAsync(MetadataV2Object resource, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FilterExpression>> ResolveExpressionsAsync(
        MetadataV2Object resource, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This row policy source does not support event delivery.");
}
