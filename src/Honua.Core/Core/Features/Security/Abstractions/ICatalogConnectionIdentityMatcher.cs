// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Security.Abstractions;

// Protocol adapters never parse connection-string syntax or expose credentials; the provider implementation owns that comparison.
public interface ICatalogConnectionIdentityMatcher
{
    bool IsMatch(string provider, string catalogConnectionString, string registeredConnectionString);
}
