// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Geoprocessing.Release;

namespace Honua.Geoprocessing.ProcessRelease;

public interface IGpProcessReleaseStore
{
    Task<GpProcessReleaseRecord> PublishAsync(GpProcessReleaseLock lockDocument, string publishedBy, CancellationToken cancellationToken = default);

    Task<(GpProcessReleaseRecord Record, bool Created)> PublishWithStatusAsync(GpProcessReleaseLock lockDocument, string publishedBy, CancellationToken cancellationToken = default);

    Task<GpProcessReleaseRecord?> GetReleaseAsync(string digest, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GpProcessReleaseRecord>> ListReleasesAsync(string? process, CancellationToken cancellationToken = default);

    Task<GpProcessBinding?> GetBindingAsync(string process, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GpProcessBinding>> ListBindingsAsync(CancellationToken cancellationToken = default);

    // A refusal is returned rather than thrown so the deployment can record it on the receipt.
    Task<GpProcessBindingDecision> ActivateAsync(GpProcessBindingActivation activation, GpProcessReleaseEnvironment environment, CancellationToken cancellationToken = default);
}
