// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Abstractions;

/// <summary>Signals that referenced output bytes cannot be accessed under their persistence contract.</summary>
public sealed class GeoprocessingOutputStoreUnavailableException : InvalidOperationException
{
    /// <summary>Creates a credential-free, retryable output-store availability failure.</summary>
    public GeoprocessingOutputStoreUnavailableException()
        : base("The referenced output store persistence attestation is missing or mismatched.")
    {
    }
}
