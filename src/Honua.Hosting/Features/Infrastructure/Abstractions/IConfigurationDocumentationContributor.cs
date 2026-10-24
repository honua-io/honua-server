// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using ConfigurationSection = Honua.Core.Configuration.ConfigurationSection;

namespace Honua.Infrastructure.Abstractions;

/// <summary>
/// Contributes feature-owned configuration metadata to the admin documentation endpoint
/// without introducing direct feature-to-feature dependencies.
/// </summary>
public interface IConfigurationDocumentationContributor
{
    /// <summary>
    /// Returns additional configuration sections for the admin documentation response.
    /// </summary>
    IReadOnlyList<ConfigurationSection> GetSections();

    /// <summary>
    /// Returns additional environment variable quick-reference entries.
    /// </summary>
    IReadOnlyList<EnvironmentVariableInfo> GetEnvironmentVariables();
}
