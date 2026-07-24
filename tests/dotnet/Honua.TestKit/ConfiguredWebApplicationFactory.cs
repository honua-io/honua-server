// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Honua.TestKit;

/// <summary>
/// Creates configured test host factories while disposing the temporary base factory
/// used by <see cref="WebApplicationFactory{TEntryPoint}.WithWebHostBuilder"/>.
/// </summary>
internal static class ConfiguredWebApplicationFactory
{
    public static WebApplicationFactory<Program> Create(
        Action<IWebHostBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return new DirectlyConfiguredFactory(configure);
    }

    private sealed class DirectlyConfiguredFactory(
        Action<IWebHostBuilder> configure) : WebApplicationFactory<Program>
    {
        private readonly Action<IWebHostBuilder> _configure = configure;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _configure(builder);
        }
    }
}
