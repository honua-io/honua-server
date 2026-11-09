// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Configuration;

public sealed class SecureConfigurationOptions
{
    public const string SectionName = "SecureConfiguration";

    public bool EncryptSensitiveValues { get; set; } = true;

    [Range(1000, 100000)]
    public int KeyDerivationIterations { get; set; } = 10000;

    public string? EncryptionSalt { get; set; }

    public bool ValidateSignatures { get; set; }

    public bool FailOnSecretValidationError { get; set; } = true;

    public bool AutoResolveSecrets { get; set; } = true;

    public HashSet<string> AllowedSources { get; set; } = new() { "appsettings.json", "environment", "commandline" };
}
