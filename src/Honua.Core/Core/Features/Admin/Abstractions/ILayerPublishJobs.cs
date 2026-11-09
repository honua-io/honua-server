// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;

namespace Honua.Core.Features.Admin.Abstractions;

public interface ILayerPublishJobs
{
    Task<LayerPublishAdmission> SubmitAsync(
        string connectionString,
        Guid connectionId,
        LayerPublishRequest request,
        string routeId,
        CancellationToken requestCancellation);

    Task<LayerPublishJobProgress?> GetAsync(string jobId, CancellationToken cancellationToken);
}
