// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Infrastructure.Abstractions;

public interface IUploadProgressStore
{
    Task SetProgressAsync(string uploadId, UploadProgress progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default);

    Task<UploadProgress?> GetProgressAsync(string uploadId, CancellationToken cancellationToken = default);

    Task DeleteProgressAsync(string uploadId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetActiveUploadIdsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UploadProgress>> GetActiveUploadsAsync(CancellationToken cancellationToken = default);
}
