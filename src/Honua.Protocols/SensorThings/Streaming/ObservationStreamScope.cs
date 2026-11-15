// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;

namespace Honua.Protocols.SensorThings.Streaming;

// Schema is part of the scope because observation identifiers are allocated independently in each database schema.
internal sealed record ObservationStreamScope(string? TenantId, string? Schema)
{
    internal static ObservationStreamScope FromServices(IServiceProvider services) => new(
        services.GetService<ITenantContext>()?.TenantId,
        services.GetService<ISchemaContext>()?.CurrentSchema);
}
