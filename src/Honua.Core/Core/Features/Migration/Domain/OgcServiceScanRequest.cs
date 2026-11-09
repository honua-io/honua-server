// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
namespace Honua.Core.Features.Migration.Domain;

public sealed record OgcServiceScanRequest
{
    public required string ServiceType { get; init; }

    public required string ServiceUrl { get; init; }

    public string? Version { get; init; }

    public int TimeoutSeconds { get; init; } = 60;

    public bool AllowUnsafeLocalUrls { get; init; }

    public string? Username { get; init; }

    public string? Password { get; init; }
}
