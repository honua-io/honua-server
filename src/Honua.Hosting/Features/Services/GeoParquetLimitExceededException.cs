// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Services;

/// <summary>A materialized GeoParquet query cannot fit the configured encoding budgets.</summary>
public sealed class GeoParquetLimitExceededException : InvalidOperationException
{
    /// <summary>Stable client-safe detail shared by protocol error adapters.</summary>
    public const string ClientMessage =
        "GeoParquet query exceeds the configured encoding budget. Request fewer rows or fields, or omit geometry.";

    /// <summary>Creates a budget rejection without exposing record values or provider details.</summary>
    public GeoParquetLimitExceededException() : base(ClientMessage)
    {
    }
}
