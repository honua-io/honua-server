// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Capabilities;

internal sealed class CapabilityGateMetadata
{
    public CapabilityGateMetadata(string descriptorId)
    {
        ArgumentException.ThrowIfNullOrEmpty(descriptorId);
        DescriptorId = descriptorId;
    }

    public string DescriptorId { get; }
}
