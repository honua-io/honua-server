// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;

namespace Honua.Tests.ProviderUnitSupport;

internal sealed class StubRowFilterSource(SqlFragment? fragment) : IRowLevelSecurityFilterSource
{
    public string? LastResourceId { get; private set; }

    public Task<SqlFragment?> ResolveAsync(MetadataV2Object resource, CancellationToken cancellationToken = default)
    {
        LastResourceId = resource.Metadata.Id;
        return Task.FromResult(fragment);
    }
}

internal sealed class StubFieldMaskSource(string[] maskedFields) : IFieldMaskSource
{
    public string? LastResourceId { get; private set; }

    public Task<ImmutableArray<string>> ResolveAsync(MetadataV2Object resource, CancellationToken cancellationToken = default)
    {
        LastResourceId = resource.Metadata.Id;
        return Task.FromResult(ImmutableArray.Create(maskedFields));
    }
}
