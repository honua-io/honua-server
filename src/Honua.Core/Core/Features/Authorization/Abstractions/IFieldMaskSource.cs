// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Authorization.Abstractions;

public interface IFieldMaskSource
{
    Task<ImmutableArray<string>> ResolveAsync(MetadataV2Object resource, CancellationToken cancellationToken = default);
}
