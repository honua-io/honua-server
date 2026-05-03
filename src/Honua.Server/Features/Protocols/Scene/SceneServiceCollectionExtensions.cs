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

        // Bind scene access signing options. ValidateOnStart fails fast if
        // the deployment ships protected scenes without a SigningKey set, so
        // we never silently fall through to an insecure issuance mode.
        // ValidateDataAnnotations enforces [Required] / [Range] declared on
        // the options type. The actual signing service still throws at
        // construction if SigningKey is empty, which is what allows the
        // scene feature to remain functional for deployments that only
        // serve public scenes (signing services are resolved lazily).
        services.AddOptions<SceneAccessSigningOptions>()
            .Bind(configuration.GetSection(SceneAccessSigningOptions.SectionName))
            .ValidateDataAnnotations();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISceneAccessEnvelopeService, SceneAccessEnvelopeService>();

        return services;
    }
}
