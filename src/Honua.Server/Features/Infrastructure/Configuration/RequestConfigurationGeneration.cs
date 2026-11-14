// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;

namespace Honua.Server.Features.Infrastructure.Configuration;

// Access decisions and cache replay share the generation captured before authentication.
internal sealed class RequestConfigurationGeneration(PostgresConfigurationDocuments documents, IHttpContextAccessor contexts)
    : IConfigurationGenerationSource
{
    internal static readonly object ContextKey = new();

    public Task<long> GetGenerationAsync(CancellationToken cancellationToken)
        => contexts.HttpContext?.Items[ContextKey] is long generation
            ? Task.FromResult(generation) : documents.GetGenerationAsync(cancellationToken);
}
