// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace Honua.TestKit.Hosting;

/// <summary>Owns a protocol fixture's in-process HTTP host and client.</summary>
public sealed class ComponentTestHost : IAsyncDisposable
{
    private readonly WebApplication _application;

    private ComponentTestHost(WebApplication application)
    {
        _application = application;
        Client = application.GetTestClient();
        Client.Timeout = TimeSpan.FromSeconds(30);
    }

    public HttpClient Client { get; }

    /// <summary>Starts a fixture-owned composition; no application services are supplied by this helper.</summary>
    public static async Task<ComponentTestHost> StartAsync(
        Action<WebApplicationBuilder> configure,
        Action<WebApplication> configurePipeline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(configurePipeline);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseTestServer();
        configure(builder);
        var application = builder.Build();
        try
        {
            configurePipeline(application);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(TimeSpan.FromSeconds(30));
            await application.StartAsync(startup.Token).ConfigureAwait(false);
            return new ComponentTestHost(application);
        }
        catch
        {
            await application.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await _application.StopAsync(shutdown.Token).ConfigureAwait(false);
        }
        finally
        {
            await _application.DisposeAsync().ConfigureAwait(false);
        }
    }
}
