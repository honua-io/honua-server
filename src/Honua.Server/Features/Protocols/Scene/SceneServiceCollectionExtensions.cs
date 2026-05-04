// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Scene.Abstractions;
using Honua.Server.Features.Infrastructure.Scene;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Protocols.Scene;

/// <summary>
/// DI registration for the hosted 3D Tiles scene serving feature.
/// </summary>
internal static class SceneServiceCollectionExtensions
{
    /// <summary>
    /// Registers the scene dataset registry and binds <see cref="SceneDatasetOptions"/>
    /// from the <c>Scenes</c> configuration section. Also wires the
    /// <see cref="ISceneAccessEnvelopeService"/> used by protected scenes to
    /// authorize browser/WebView nested asset cascades.
    /// </summary>
    public static IServiceCollection AddScene(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<SceneDatasetOptions>(configuration.GetSection(SceneDatasetOptions.SectionName));
        services.TryAddSingleton<ISceneDatasetRegistry, ConfigurationSceneDatasetRegistry>();

        // Bind scene access signing options without ValidateOnStart() and
        // without ValidateDataAnnotations(): deployments that serve only
        // public scenes (no AccessPolicy on any registered scene) must be
        // able to start without a SigningKey configured, and data-annotation
        // validation throws OptionsValidationException, which is not a
        // subclass of InvalidOperationException and would silently bypass
        // the SceneAccessEnvelopeService constructor's structured fail-closed
        // path. The constructor enforces all bounds (signing-key presence,
        // TTL range, refresh fraction range) as InvalidOperationException
        // throws, which the scene endpoints catch at the resolve site to
        // surface a structured 500 + OptionsMisconfigured (8415) log so a
        // misconfigured protected-scene deployment is operationally visible
        // without taking the rest of the server down.
        services.AddOptions<SceneAccessSigningOptions>()
            .Bind(configuration.GetSection(SceneAccessSigningOptions.SectionName));

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISceneAccessEnvelopeService, SceneAccessEnvelopeService>();

        return services;
    }
}
