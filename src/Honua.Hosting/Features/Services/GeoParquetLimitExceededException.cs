// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Services;

public sealed class GeoParquetLimitExceededException : InvalidOperationException
{
    public const string ClientMessage =
        "GeoParquet query exceeds the configured encoding budget. Request fewer rows or fields, or omit geometry.";

    public GeoParquetLimitExceededException() : base(ClientMessage)
    {
    }
}
