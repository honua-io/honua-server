// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Configuration;

namespace Honua.Server.Features.Admin.Abstractions;

public interface IConfigurationDocumentationContributor
{
    IEnumerable<string> GetConfigurationSections();

    Task<ConfigurationSectionDocumentation> GenerateDocumentationAsync(string sectionName, IConfiguration configuration);
}

public sealed record ConfigurationSectionDocumentation(
    string SectionName,
    string Description,
    IReadOnlyDictionary<string, ConfigurationPropertyDocumentation> Properties,
    IReadOnlyList<string> Examples);

public sealed record ConfigurationPropertyDocumentation(
    string PropertyName,
    string Description,
    Type PropertyType,
    object? DefaultValue,
    bool IsRequired,
    string? ValidationRules);
