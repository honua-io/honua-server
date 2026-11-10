// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

public sealed class DuplicateVersionNameException : Exception
{
    public string VersionName { get; }

    public DuplicateVersionNameException(string versionName)
        : base($"A version named '{versionName}' already exists.")
        => VersionName = versionName;

    public DuplicateVersionNameException(string versionName, Exception innerException)
        : base($"A version named '{versionName}' already exists.", innerException)
        => VersionName = versionName;
}
