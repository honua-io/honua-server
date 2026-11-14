// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Core.Features.Metadata.Caching;

public sealed record MetadataV2GraphCacheConfiguration
{
    public required string Environment { get; init; }
    public required IServiceScopeFactory LoadScopeFactory { get; init; }
    public required Func<IServiceProvider, IMetadataV2GraphProvider> ResolveLoader { get; init; }
    public IMetadataV2GraphRevisionProbe? RevisionProbe { get; init; }
    public IConfigurationGenerationSource? GenerationSource { get; init; }
}
