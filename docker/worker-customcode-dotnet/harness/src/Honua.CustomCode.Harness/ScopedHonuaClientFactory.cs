// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.CustomCode.Harness;

/// <summary>Builds published, typed SDK clients carrying the job-bound capability.</summary>
/// <remarks>
/// <see cref="Sdk.GpContext.Client"/> is an <see cref="IServiceProvider"/>; tools resolve
/// IHonuaOgcFeaturesClient or the FeatureServer client from it. The token is an intended
/// job capability visible to same-process user code. Environment cleanup is not isolation;
/// credential-free worker configuration and server-side scope/attempt checks provide the boundary.
/// </remarks>
public static class ScopedHonuaClientFactory
{
    /// <summary>Creates the typed SDK service provider for a running job.</summary>
    /// <param name="baseUrl">Pinned server origin.</param>
    /// <param name="jobToken">The intended job capability.</param>
    /// <param name="jobId">Job context required by server authentication.</param>
    /// <returns>A disposable provider of typed OGC Features and FeatureServer SDK clients.</returns>
    public static ServiceProvider Create(string baseUrl, string jobToken, string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        var origin = new Uri(baseUrl, UriKind.Absolute);
        if (origin.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(origin.UserInfo))
        {
            throw new ArgumentException("The scoped client requires an HTTP(S) server URL without user information.", nameof(baseUrl));
        }

        var services = new ServiceCollection();
        services.AddHonua(options =>
        {
            options.BaseAddress = origin;
            options.BearerToken = jobToken;
            options.UseGrpc = false;
            options.UseAdmin = false;
            options.UseGeocoding = false;
            options.UseProcesses = false;
            options.UseWfs = false;
            options.UseOgcFeatures = true;
            options.UseGeoServices = true;
            options.EnableRetry = false;
            options.PrimaryHttpMessageHandlerFactory = () => new JobContextHandler(origin, jobId)
            {
                InnerHandler = new HttpClientHandler { AllowAutoRedirect = false },
            };
        });
        return services.BuildServiceProvider();
    }

    private sealed class JobContextHandler(Uri origin, string jobId) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var destination = request.RequestUri;
            if (destination is null || !destination.IsAbsoluteUri ||
                !string.Equals(destination.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(destination.Host, origin.Host, StringComparison.OrdinalIgnoreCase) || destination.Port != origin.Port)
            {
                throw new InvalidOperationException("Scoped SDK requests must remain on the configured server origin.");
            }

            request.Headers.Remove("X-Honua-Job-Id");
            request.Headers.Add("X-Honua-Job-Id", jobId);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
