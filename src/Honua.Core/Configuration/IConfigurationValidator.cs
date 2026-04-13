// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Configuration;

namespace Honua.Core.Configuration;

/// <summary>
/// Interface for validating configuration settings.
/// </summary>
public interface IConfigurationValidator
{
    /// <summary>
    /// Validates the configuration and returns any validation errors.
    /// </summary>
    /// <param name="configuration">The configuration to validate</param>
    /// <returns>List of validation error messages</returns>
    IEnumerable<string> ValidateConfiguration(IConfiguration configuration);

    /// <summary>
    /// Gets the configuration section this validator applies to.
    /// </summary>
    string ConfigurationSection { get; }
}