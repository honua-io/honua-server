// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Abstractions;

// Notified when administrative changes (access policy, filters, fields, or catalog updates)
// may make an already-promoted public Export ineligible for anonymous delivery.
public interface IPublicExportAccessChangeListener
{
    Task OnAdministrativeAccessChangeAsync(
        string? serviceId,
        IReadOnlyCollection<int>? layerIds,
        CancellationToken cancellationToken = default);
}
