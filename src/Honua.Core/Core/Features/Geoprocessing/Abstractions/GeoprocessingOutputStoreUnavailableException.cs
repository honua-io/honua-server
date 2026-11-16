// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Abstractions;

public sealed class GeoprocessingOutputStoreUnavailableException : InvalidOperationException
{
    public GeoprocessingOutputStoreUnavailableException()
        : base("The referenced output store persistence attestation is missing or mismatched.")
    {
    }
}
