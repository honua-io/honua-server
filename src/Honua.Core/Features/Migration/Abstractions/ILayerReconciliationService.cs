// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Features.Migration.Abstractions;

// A hard-check failure holds the run in NeedsReview, and probes use the apply-time snapshot instead of calling the source again.
public interface ILayerReconciliationService
{
    Task<MigrationReconciliationArtifact> ReconcileAsync(
        LayerReconciliationRequest request,
        CancellationToken cancellationToken = default);
}
