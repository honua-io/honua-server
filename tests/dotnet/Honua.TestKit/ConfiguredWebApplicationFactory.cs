// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Mvc.Testing;

namespace Honua.TestKit;

/// <summary>
/// Creates configured test host factories while disposing the temporary base factory
/// used by <see cref="WebApplicationFactory{TEntryPoint}.WithWebHostBuilder"/>.
/// </summary>
internal static class ConfiguredWebApplicationFactory
{
    public static WebApplicationFactory<Program> Create(
        Action<Microsoft.AspNetCore.Hosting.IWebHostBuilder> configure)
    {
        using var factory = new WebApplicationFactory<Program>();
        return factory.WithWebHostBuilder(configure);
    }
}
