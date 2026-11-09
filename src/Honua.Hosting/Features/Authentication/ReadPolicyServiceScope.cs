// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.AspNetCore.Http;

namespace Honua.Infrastructure.Authentication;

internal readonly record struct ReadPolicyMetadataContext(HttpContext? Context, IMetadataV2GraphProvider Provider);

internal static class ReadPolicyServiceScope
{
    internal static async Task<IReadOnlyCollection<string>> ResolveAsync(
        MetadataV2Object resource,
        ReadPolicyMetadataContext metadata,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = ValidatedMetadataSnapshot.Find(metadata.Context, resource)
            ?? await metadata.Provider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.Index.ServiceNamesByResource.TryGetValue(resource.Metadata.Id, out var names)
            ? names
            : Array.Empty<string>();
    }
}
