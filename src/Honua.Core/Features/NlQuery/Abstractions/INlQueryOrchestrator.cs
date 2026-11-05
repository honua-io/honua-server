// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.NlQuery.Domain;

namespace Honua.Core.Features.NlQuery.Abstractions;

public interface INlQueryOrchestrator
{
    Task<NlQueryOrchestrationResult> ExecuteAsync(
        NlQueryPlanRequest request,
        CancellationToken cancellationToken = default);
}
