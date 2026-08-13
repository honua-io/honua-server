// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace Honua.TestKit;

/// <summary>
/// Creates configured test host factories while disposing the temporary base factory
/// used by <see cref="WebApplicationFactory{TEntryPoint}.WithWebHostBuilder"/>.
/// </summary>
internal static class ConfiguredWebApplicationFactory
{
    private static readonly object _testEnvironmentGate = new();

    public static WebApplicationFactory<Program> Create(
        Action<IWebHostBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return new DirectlyConfiguredFactory(configure);
    }

    /// <summary>
    /// Starts a test host with the early environment values <c>Program.cs</c> reads before
    /// <see cref="WebApplicationFactory{TEntryPoint}.ConfigureWebHost"/> runs. The process-wide
    /// values are serialized, preserved, and restored as soon as host startup completes so a
    /// later Production or Staging factory cannot inherit the Test environment.
    /// </summary>
    internal static IHost StartInTestEnvironment(Func<IHost> startHost)
        => StartInEnvironment(startHost, "Test", enableTestSchemaHeaders: true);

    /// <summary>
    /// Starts a host with the environment value that <c>Program.cs</c> must observe before
    /// later web-host callbacks run.
    /// </summary>
    internal static IHost StartInEnvironment(
        Func<IHost> startHost,
        string environmentName,
        bool enableTestSchemaHeaders)
    {
        ArgumentNullException.ThrowIfNull(startHost);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);

        lock (_testEnvironmentGate)
        {
            var dotnetEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
            var aspnetCoreEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            var schemaHeaders = Environment.GetEnvironmentVariable("HONUA_TEST_SCHEMA_HEADERS");
            try
            {
                Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", environmentName);
                Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", environmentName);
                if (enableTestSchemaHeaders)
                {
                    Environment.SetEnvironmentVariable("HONUA_TEST_SCHEMA_HEADERS", "true");
                }
                else
                {
                    Environment.SetEnvironmentVariable("HONUA_TEST_SCHEMA_HEADERS", null);
                }
                return startHost();
            }
            finally
            {
                Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", dotnetEnvironment);
                Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", aspnetCoreEnvironment);
                Environment.SetEnvironmentVariable("HONUA_TEST_SCHEMA_HEADERS", schemaHeaders);
            }
        }
    }

    private sealed class DirectlyConfiguredFactory(
        Action<IWebHostBuilder> configure) : WebApplicationFactory<Program>
    {
        private readonly Action<IWebHostBuilder> _configure = configure;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _configure(builder);
        }

        protected override IHost CreateHost(IHostBuilder builder)
            => StartInTestEnvironment(() => base.CreateHost(builder));
    }
}
