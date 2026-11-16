// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Publishing.Descriptors;

namespace Honua.Server.Features.Admin.PairedPublications;

internal sealed class PairedPublicationRefreshDecision(IChangeTracker changes)
{
    internal async Task<bool> IsCurrentAsync(
        PublicationDescriptor? descriptor, PublicationRefreshSource target, CancellationToken cancellationToken)
    {
        var (source, metadataRevision, storageLayerId) = target;
        if (descriptor is null || descriptor.Validation.Status != PublicationValidation.Passed
            || descriptor.SourceLayer.Service != source.Service || descriptor.SourceLayer.Layer != source.Layer)
        {
            return false;
        }
        var prefix = string.Create(CultureInfo.InvariantCulture, $"metadata-{metadataRevision}:changes-");
        var revision = descriptor.Capture.SourceRevision;
        return revision.StartsWith(prefix, StringComparison.Ordinal)
            && long.TryParse(revision.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var generation)
            && !await changes.HasLayerChangedSinceAsync(generation, storageLayerId, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed record PublicationRefreshSource(PublicationSourceLayer Source, long MetadataRevision, int StorageLayerId);
