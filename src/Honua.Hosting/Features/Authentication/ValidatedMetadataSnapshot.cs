// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.AspNetCore.Http;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Carries metadata already used for a successful authorization in this request.
/// Only the identical canonical resource can reuse it; this contains no principal,
/// evaluated predicate or policy set. Other resources and background reads resolve
/// their own current metadata. A later validation replaces the request feature.
/// </summary>
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
