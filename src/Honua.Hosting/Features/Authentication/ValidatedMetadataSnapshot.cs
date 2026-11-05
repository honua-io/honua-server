// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.AspNetCore.Http;

namespace Honua.Infrastructure.Authentication;

internal sealed class ValidatedMetadataSnapshot(MetadataV2Resource resource, MetadataV2GraphSnapshot snapshot)
{
    private readonly MetadataV2Resource _resource = resource;
    private readonly MetadataV2GraphSnapshot _snapshot = snapshot;

    internal static void Remember(HttpContext context, MetadataV2Resource resource, MetadataV2GraphSnapshot snapshot)
        => context.Features.Set(new ValidatedMetadataSnapshot(resource, snapshot));

    internal static MetadataV2GraphSnapshot? Find(HttpContext? context, MetadataV2Resource resource)
    {
        var validated = context?.Features.Get<ValidatedMetadataSnapshot>();
        return validated is not null && ReferenceEquals(validated._resource, resource)
            ? validated._snapshot
            : null;
    }
}
