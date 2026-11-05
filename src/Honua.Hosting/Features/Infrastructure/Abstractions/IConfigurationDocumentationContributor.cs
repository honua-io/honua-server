// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using ConfigurationSection = Honua.Core.Configuration.ConfigurationSection;

namespace Honua.Infrastructure.Abstractions;

public interface IConfigurationDocumentationContributor
{
    IReadOnlyList<ConfigurationSection> GetSections();

    IReadOnlyList<EnvironmentVariableInfo> GetEnvironmentVariables();
}
