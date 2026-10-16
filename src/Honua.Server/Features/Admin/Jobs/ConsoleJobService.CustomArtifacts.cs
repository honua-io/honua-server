// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Geoprocessing.CustomCode;

namespace Honua.Server.Features.Admin.Jobs;

internal sealed partial class ConsoleJobService
{
    private async Task<ConsoleJobArtifact?> MapCustomCodeArtifactAsync(string reference, CancellationToken cancellationToken)
    {
        if (!CustomCodeArtifactDescriptor.TryParse(reference, out var artifact))
        {
            return null;
        }

        var availability = "Unavailable";
        if (artifact!.State == "available" && serviceProvider.GetService<ICloudFileStorage>() is { } storage)
        {
            try
            {
                var file = await storage.GetMetadataAsync(artifact.ObjectKey, cancellationToken).ConfigureAwait(false);
                availability = file is null || file.ExpiresAt <= timeProvider.GetUtcNow()
                    ? "Expired"
                    : file.SizeBytes == artifact.SizeBytes ? "Available" : "Unavailable";
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                ArtifactLookupFailed(logger, artifact.Name, exception);
                availability = "ProviderError";
            }
        }

        return new ConsoleJobArtifact
        {
            ArtifactId = $"custom:{artifact.JobId}:{artifact.Attempt}:{artifact.Name}",
            Availability = availability,
            Kind = "CustomCodeOutput",
            Label = artifact.Name,
            ContentType = artifact.ContentType,
            SizeBytes = artifact.SizeBytes,
            ProviderLink = availability == "Available" ? artifact.ContentUrl : null,
            Message = availability == "Available" ? null : "Artifact bytes are expired, unavailable, or the upload did not complete.",
        };
    }
}
