// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using ConfigurationSection = Honua.Core.Configuration.ConfigurationSection;

namespace Honua.Server.Features.Infrastructure.Abstractions;

/// <summary>
/// Contributes feature-owned configuration metadata to the admin documentation endpoint
/// without introducing direct feature-to-feature dependencies.
/// </summary>
internal interface IConfigurationDocumentationContributor
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
