// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Configuration;

public interface IConfigurationValidator
{
    IEnumerable<string> ValidateConfiguration(IConfiguration configuration);

    ConfigurationValidationResult ValidateOptions<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions>(
        TOptions options,
        string sectionName,
        bool isDevelopment = false)
        where TOptions : class;

    Task<ConfigurationValidationSummary> ValidateAllAsync(bool isDevelopment = false, bool isTest = false);

    void RegisterOptionsType<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions>(
        string sectionName,
        bool isRequired = true)
        where TOptions : class;

    ConfigurationOptionsMetadata GetOptionsMetadata<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions>()
        where TOptions : class;

    string ConfigurationSection { get; }
}
