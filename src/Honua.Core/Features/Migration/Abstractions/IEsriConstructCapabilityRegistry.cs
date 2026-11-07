// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Features.Migration.Abstractions;

public interface IEsriConstructCapabilityRegistry
{
    GeoServicesConstructCapabilityDescriptor ResolveOrUnknown(string constructKey);

    IReadOnlyCollection<string> RegisteredConstructKeys { get; }
}
