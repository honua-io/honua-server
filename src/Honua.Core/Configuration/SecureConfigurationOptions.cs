// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Configuration;

/// <summary>
/// Configuration options for secure configuration handling.
/// </summary>
public sealed class SecureConfigurationOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "SecureConfiguration";

    /// <summary>
    /// Whether to encrypt sensitive configuration values.
    /// </summary>
    public bool EncryptSensitiveValues { get; init; } = true;

    /// <summary>
    /// Key derivation iterations for encryption.
    /// </summary>
    [Range(1000, 100000)]
    public int KeyDerivationIterations { get; init; } = 10000;

    /// <summary>
    /// Salt for key derivation.
    /// </summary>
    public string? EncryptionSalt { get; init; }

    /// <summary>
    /// Whether to validate configuration signatures.
    /// </summary>
    public bool ValidateSignatures { get; init; } = false;

    /// <summary>
    /// Allowed configuration sources.
    /// </summary>
    public HashSet<string> AllowedSources { get; init; } = new() { "appsettings.json", "environment", "commandline" };
}