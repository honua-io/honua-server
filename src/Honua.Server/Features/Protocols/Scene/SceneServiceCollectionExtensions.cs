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

        // Bind scene access signing options. ValidateOnStart() is
        // intentionally NOT called: deployments that serve only public
        // scenes (no AccessPolicy on any registered scene) must be able
        // to start without a SigningKey configured. ValidateDataAnnotations
        // enforces [Range] on the TTL/refresh fields lazily on the first
        // IOptions resolve. The SigningKey presence check is intentionally
        // NOT a [Required] data annotation — that would surface as
        // OptionsValidationException from IOptions.Value, which the endpoint
        // handlers' catch (InvalidOperationException) blocks would not
        // catch, silencing the structured SigningMisconfigured (8415) log.
        // Instead the SceneAccessEnvelopeService constructor is the
        // runtime fail-closed guard — it throws InvalidOperationException
        // if SigningKey is unset, and the scene endpoints catch that at
        // the resolve site and surface a structured 500 + log entry so a
        // misconfigured protected-scene deployment is operationally
        // visible without taking the rest of the server down.
        services.AddOptions<SceneAccessSigningOptions>()
            .Bind(configuration.GetSection(SceneAccessSigningOptions.SectionName))
            .ValidateDataAnnotations();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISceneAccessEnvelopeService, SceneAccessEnvelopeService>();

        return services;
    }
}
