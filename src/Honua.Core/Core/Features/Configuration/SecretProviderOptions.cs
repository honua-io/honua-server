// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Configuration;

public sealed class SecretProviderOptions
{
    public const string SectionName = "SecretProvider";

    public bool EnableCaching { get; set; } = true;

    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(5);

    public int MaxCacheSize { get; set; } = 256;

    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public bool LogSecretAccess { get; set; } = true;
}
