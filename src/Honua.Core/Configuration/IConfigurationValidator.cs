// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using System.ComponentModel.DataAnnotations;

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
    /// Validates a bound options instance.
    /// </summary>
    /// <typeparam name="TOptions">The options type to validate.</typeparam>
    /// <param name="options">The options instance.</param>
    /// <param name="sectionName">The configuration section name.</param>
    /// <param name="isDevelopment">Whether validation is running in development mode.</param>
    /// <returns>The validation result.</returns>
    ConfigurationValidationResult ValidateOptions<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions>(
        TOptions options,
        string sectionName,
        bool isDevelopment = false)
        where TOptions : class;

    /// <summary>
    /// Validates all registered configuration types.
    /// </summary>
    /// <param name="isDevelopment">Whether validation is running in development mode.</param>
    /// <param name="isTest">Whether validation is running in a test environment.</param>
    /// <returns>A summary of validation results.</returns>
    Task<ConfigurationValidationSummary> ValidateAllAsync(bool isDevelopment = false, bool isTest = false);

    /// <summary>
    /// Registers an options type for later validation and discovery.
    /// </summary>
    /// <typeparam name="TOptions">The options type.</typeparam>
    /// <param name="sectionName">The configuration section name.</param>
    /// <param name="isRequired">Whether the section is required.</param>
    void RegisterOptionsType<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions>(
        string sectionName,
        bool isRequired = true)
        where TOptions : class;

    /// <summary>
    /// Gets metadata for a registered options type.
    /// </summary>
    /// <typeparam name="TOptions">The options type.</typeparam>
    /// <returns>The metadata for the options type.</returns>
    ConfigurationOptionsMetadata GetOptionsMetadata<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions>()
        where TOptions : class;

    /// <summary>
    /// Gets the configuration section this validator applies to.
    /// </summary>
    string ConfigurationSection { get; }
}
