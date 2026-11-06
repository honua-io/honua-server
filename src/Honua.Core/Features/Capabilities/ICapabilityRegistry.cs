// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Capabilities;

public interface ICapabilityRegistry
{
    IReadOnlyList<CapabilityDescriptor> All { get; }

    CapabilityDescriptor? Find(string id);

    CapabilityResolution Resolve(string id, CapabilityGateContext context);
}
