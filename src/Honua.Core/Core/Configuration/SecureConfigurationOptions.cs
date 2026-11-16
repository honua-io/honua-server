// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Configuration;

public sealed class SecureConfigurationOptions
{
    public const string SectionName = "SecureConfiguration";

    public bool FailOnSecretValidationError { get; set; } = true;
}
