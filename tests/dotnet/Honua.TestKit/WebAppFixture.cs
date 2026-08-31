// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Queries.Filters;
using Honua.TestKit.Infrastructure;
using Honua.TestKit.Seeding;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.TestKit;

/// <summary>
/// Web application fixture for integration tests.
/// Combines WebApplicationFactory with PostgresFixture.
/// Supports service replacement and schema-based isolation.
/// </summary>
public sealed class WebAppFixture : IAsyncLifetime
{
    // Audit-A2: the process-wide shared-factory/Postgres/refcount/semaphore static
    // state used to live here. It is owned by
    // <see cref="Mixins.WebAppFixtureSharedBootstrapMixin"/> now, which exposes the
    // factory + Postgres handles through static accessors. The bootstrap mixin owns
    // the lifecycle (init + ref-counted release) so the per-fixture instance code
    // only needs to know about its own per-test state.

    private PostgresFixture? _postgres;
    private readonly List<Action<IServiceCollection>> _serviceConfigurations = [];
    private readonly Dictionary<Type, object> _scopedServiceOverrides = [];
    private Action<IWebHostBuilder>? _configureWebHost;
    private WebApplicationFactory<Program>? _factory;
    private string? _currentSchema;
    private string _environmentName = "Test";
    private bool _useSharedServer;
    private string? _seedPath;
    private string? _seedProfile;
    private IServiceScope? _serviceScope;
    private string? _serviceOverrideScopeId;

    /// <summary>
    /// Test service ID used for testing operations.
    /// </summary>
    public const string TestServiceId = "test";

    /// <summary>
    /// Test layer ID used for testing operations.
    /// </summary>
    public const int TestLayerId = 0;

    // Audit-A2: the geocoding base URL and encryption material constants used to live
    // here as static strings. They now live on
    // <see cref="WebAppFixturePostgresWiringMixin"/> next to the configuration-building
    // helpers that read them.

    // Audit-A2: the default GeoServices drawingInfo, V2 graph factories, and per-layer
    // schema/spatial/temporal helpers historically lived inline as ~770 LOC of static
    // helpers. They are pure functions over layer ids and seed-file names — no fixture
    // state — and now live on Honua.TestKit.Mixins.WebAppFixtureMetadataV2Mixin.

    /// <summary>
    /// Admin password used by <see cref="CreateAdminClient"/> and configured into the
    /// shared test server's <c>HONUA_ADMIN_PASSWORD</c> setting. Exposed so tests that
    /// build their own non-shared <see cref="WebAppFixture"/> (e.g. cross-client
    /// certification fixtures) can wire the same value into their custom web host
    /// configuration without re-declaring a string literal that must stay in lockstep.
    /// </summary>
    public const string SharedAdminPassword = "test-admin-password";

    /// <summary>
    /// Stable actor identifier assigned to the password-based bootstrap admin by
    /// the API-key authentication handler. Tests that seed ownership, proposal,
    /// or audit records for <see cref="CreateAdminClient"/> must use this value.
    /// </summary>
    public const string SharedAdminActorId = "00000000-0000-0000-0000-000000000002";

    /// <summary>
    /// Stable actor identifier assigned when an isolated test host explicitly
    /// enables the development authentication bypass.
    /// </summary>
    public const string DevelopmentBypassActorId = "00000000-0000-0000-0000-000000000001";
    private static readonly TimeSpan _defaultTestClientTimeout = TimeSpan.FromMinutes(5);

    public HttpClient Client { get; private set; } = null!;

    public PostgresFixture Postgres => _useSharedServer
        ? Honua.TestKit.Mixins.WebAppFixtureSharedBootstrapMixin.Postgres
        : _postgres ?? throw new InvalidOperationException("Postgres fixture not initialized.");

    public PostgresFixture PostgresFixture => Postgres;

    public string? CurrentSchema => _currentSchema;

    // HTTP clients always send the fixture schema header, including for isolated hosts.
    // Keep out-of-request graph reads and writes on that same partition so a request-side
    // IMetadataV2GraphStore.SaveAsync does not fork away from the fixture's baseline view.
    internal string? MetadataGraphSchema => _currentSchema;

    /// <summary>
    /// Gets the database connection provider for test scenarios.
    /// </summary>
    public IDatabaseConnectionProvider DatabaseConnectionProvider => GetService<IDatabaseConnectionProvider>();

    /// <summary>
    /// Gets the service provider from the test server's DI container.
    /// </summary>
    public IServiceProvider Services => ActiveFactory.Services;

    private bool HasCustomConfiguration => _serviceConfigurations.Count > 0
        || _configureWebHost != null
        || !string.Equals(_environmentName, "Test", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the active <see cref="WebApplicationFactory{TEntryPoint}"/> for this
    /// fixture, sourced from the shared bootstrap mixin when running in shared mode
    /// or this instance's <c>_factory</c> in isolated mode. Throws when called before
    /// initialization.
    /// </summary>
    private WebApplicationFactory<Program> ActiveFactory => _useSharedServer
        ? Honua.TestKit.Mixins.WebAppFixtureSharedBootstrapMixin.Factory
        : _factory ?? throw new InvalidOperationException("Web application factory not initialized.");

    public async Task InitializeAsync()
    {
        _useSharedServer = !HasCustomConfiguration;

        if (_useSharedServer)
        {
            await InitializeSharedAsync();
            return;
        }

        _postgres = new PostgresFixture();
        await _postgres.InitializeAsync();

        // Not disposed here by design: this factory is stored in the instance field
        // _factory and disposed once in DisposeAsync (see below), which owns its lifetime
        // for the whole fixture. Wrapping this in `using` would dispose it before the
        // fixture's tests run.
        _factory = ConfiguredWebApplicationFactory.Create(builder =>
            {
                // Audit-A2: host settings, app configuration, and PostgreSQL test
                // services wiring all live on WebAppFixturePostgresWiringMixin so the
                // isolated and shared bootstrap paths stay in lockstep.
                Honua.TestKit.Mixins.WebAppFixturePostgresWiringMixin.ApplyCommonHostSettings(builder);

                _configureWebHost?.Invoke(builder);

                builder.ConfigureAppConfiguration((context, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(
                        Honua.TestKit.Mixins.WebAppFixturePostgresWiringMixin
                            .BuildAppConfigurationDictionary(_postgres.ConnectionString));
                });

                builder.ConfigureTestServices(services =>
                {
                    Honua.TestKit.Mixins.WebAppFixturePostgresWiringMixin.ConfigureIsolatedTestServices(
                        services,
                        _postgres.ConnectionString,
                        () => _currentSchema,
                        _serviceConfigurations);

                    // An isolated fixture owns its host, so request overrides can be
                    // installed directly without the shared host's header-based registry.
                    foreach (var (serviceType, instance) in _scopedServiceOverrides)
                    {
                        services.RemoveAll(serviceType);
                        services.AddSingleton(serviceType, instance);
                    }
                });
            },
            _environmentName);

        Client = CreateClient();
        _serviceScope = _factory.Services.CreateScope();

        if (string.IsNullOrWhiteSpace(_currentSchema))
        {
            _currentSchema = await _postgres.CreateIsolatedSchemaAsync(nameof(WebAppFixture));
            await SeedSchemaAsync(_currentSchema);
        }
        ApplyCurrentSchemaHeader(Client);

        ApplySeedSpecificMetadataV2Graph();

        await EnsureTestSecureConnectionAsync();
    }

    /// <summary>
    /// Ensures a large test dataset exists for streaming performance tests. Delegates to
    /// <see cref="Mixins.WebAppFixtureLargeDatasetMixin"/>.
    /// </summary>
    public Task EnsureLargeTestDatasetAsync()
        => Honua.TestKit.Mixins.WebAppFixtureLargeDatasetMixin.EnsureLargeTestDatasetAsync(
            Postgres,
            _currentSchema!,
            TestLayerId);

    // Audit-A2 follow-up: the V2 graph mutation helpers (UpdateV2*, SetV2*, and
    // AddAdminSampleMetadataV2Graph) used to live inline as ~400 LOC. They now live on
    // Honua.TestKit.Mixins.WebAppFixtureMetadataV2GraphMutationMixin and the methods below
    // are thin delegating wrappers that preserve the public fixture API. ApplySeedSpecificMetadataV2Graph
    // is likewise routed through the mixin.

    private void ApplySeedSpecificMetadataV2Graph()
    {
        if (_serviceScope is null)
        {
            return;
        }

        Honua.TestKit.Mixins.WebAppFixtureMetadataV2GraphMutationMixin.ApplySeedSpecificGraph(this, _seedPath);
    }

    /// <summary>
    /// V2-aware helper that mirrors the v1 <c>ILayerMetadataUpdater</c> seed surface used
    /// by classic-protocol and STAC tests. Delegates to
    /// <see cref="Mixins.WebAppFixtureMetadataV2GraphMutationMixin"/>.
    /// </summary>
    public void UpdateV2ResourceMetadata(
        int layerIndex,
        AccessPolicy? accessPolicy = null,
        MetadataV2ResourceTemporal? temporal = null,
        MetadataV2ResourceSpatial? spatial = null,
        MetadataV2PermanentFilter? permanentFilter = null,
        MetadataV2ExtrusionInfo? extrusion = null,
        JsonElement? stacExtension = null,
        bool clearAccessPolicy = false,
        bool clearTemporal = false,
        bool clearPermanentFilter = false,
        bool clearExtrusion = false)
        => Honua.TestKit.Mixins.WebAppFixtureMetadataV2GraphMutationMixin.UpdateResourceMetadata(
            this,
            layerIndex,
            accessPolicy,
            temporal,
            spatial,
            permanentFilter,
            extrusion,
            stacExtension,
            clearAccessPolicy,
            clearTemporal,
            clearPermanentFilter,
            clearExtrusion);

    /// <summary>
    /// Reads the current Metadata v2 graph snapshot for this fixture's graph partition.
    /// </summary>
    public MetadataV2GraphSnapshot GetCurrentV2GraphSnapshot()
        => Honua.TestKit.Mixins.WebAppFixtureMetadataV2GraphMutationMixin.GetCurrentSnapshot(this);

    /// <summary>
    /// Mutates the common metadata block for the canonical resource published at
    /// <paramref name="layerIndex"/>.
    /// </summary>
    public void MutateV2ResourceObjectMetadata(
        int layerIndex,
        Func<MetadataV2ObjectMetadata, MetadataV2ObjectMetadata> mutate)
        => Honua.TestKit.Mixins.WebAppFixtureMetadataV2GraphMutationMixin.MutateResourceObjectMetadata(
            this,
            layerIndex,
            mutate);

    /// <summary>
    /// Adds or replaces a Metadata v2 schema field on the resource published at
    /// <paramref name="layerIndex"/>. Delegates to
    /// <see cref="Mixins.WebAppFixtureMetadataV2GraphMutationMixin"/>.
    /// </summary>
    public void UpdateV2ResourceSchemaField(int layerIndex, MetadataV2Field field)
        => Honua.TestKit.Mixins.WebAppFixtureMetadataV2GraphMutationMixin.UpdateResourceSchemaField(this, layerIndex, field);

    /// <summary>
    /// Sets (or clears with <c>null</c>) the Esri subtype set on the resource published
    /// at <paramref name="layerIndex"/>. Delegates to
    /// <see cref="Mixins.WebAppFixtureMetadataV2GraphMutationMixin"/>.
    /// </summary>
    public void UpdateV2ResourceSubtypes(int layerIndex, MetadataV2Subtypes? subtypes)
        => Honua.TestKit.Mixins.WebAppFixtureMetadataV2GraphMutationMixin.UpdateResourceSubtypes(this, layerIndex, subtypes);

    /// <summary>
    /// Sets (or clears with <c>null</c>) the Esri attribute-rule set on the resource
    /// published at <paramref name="layerIndex"/>. Delegates to
    /// <see cref="Mixins.WebAppFixtureMetadataV2GraphMutationMixin"/>.
    /// </summary>
    public Task UpdateV2ResourceAttributeRulesAsync(
        int layerIndex,
        IReadOnlyList<MetadataV2AttributeRule>? attributeRules,
        CancellationToken cancellationToken = default)
        => Honua.TestKit.Mixins.WebAppFixtureMetadataV2GraphMutationMixin.UpdateResourceAttributeRulesAsync(
            this,
            layerIndex,
            attributeRules,
            cancellationToken);

    /// <summary>
    /// Sets (or clears with <c>null</c>) the Esri contingent-value groups on the resource
    /// published at <paramref name="layerIndex"/>. Delegates to
    /// <see cref="Mixins.WebAppFixtureMetadataV2GraphMutationMixin"/>.
    /// </summary>
    public Task UpdateV2ResourceContingentValueGroupsAsync(
        int layerIndex,
        IReadOnlyList<MetadataV2ContingentValueGroup>? contingentValueGroups,
        CancellationToken cancellationToken = default)
        => Honua.TestKit.Mixins.WebAppFixtureMetadataV2GraphMutationMixin.UpdateResourceContingentValueGroupsAsync(
            this,
            layerIndex,
            contingentValueGroups,
            cancellationToken);

    /// <summary>
    /// V2-aware helper that renames the canonical resource bound to the publication with
    /// <paramref name="layerIndex"/>. Delegates to
    /// <see cref="Mixins.WebAppFixtureMetadataV2GraphMutationMixin"/>.
    /// </summary>
    public void UpdateV2ResourceName(int layerIndex, string newName)
        => Honua.TestKit.Mixins.WebAppFixtureMetadataV2GraphMutationMixin.UpdateResourceName(this, layerIndex, newName);

    /// <summary>
    /// V2-aware helper that mirrors the v1 <c>IServiceMetadataUpdater</c> seed surface
    /// for service-level toggles. Delegates to
    /// <see cref="Mixins.WebAppFixtureMetadataV2GraphMutationMixin"/>.
    /// </summary>
    public void UpdateV2ServiceMetadata(
        string serviceName,
        IReadOnlyList<string>? enabledProtocols = null,
        AccessPolicy? accessPolicy = null,
        bool clearAccessPolicy = false)
        => Honua.TestKit.Mixins.WebAppFixtureMetadataV2GraphMutationMixin.UpdateServiceMetadata(
            this,
            serviceName,
            enabledProtocols,
            accessPolicy,
            clearAccessPolicy);

    /// <summary>
    /// Toggles the runtime visibility of the Metadata v2 publications/resources bound to
    /// the supplied layer index. Delegates to
    /// <see cref="Mixins.WebAppFixtureMetadataV2GraphMutationMixin"/>.
    /// </summary>
    public void SetV2LayerEnabled(int layerIndex, bool enabled)
        => Honua.TestKit.Mixins.WebAppFixtureMetadataV2GraphMutationMixin.SetLayerEnabled(this, layerIndex, enabled);

    /// <summary>
    /// Advertises the supplied edit capabilities (e.g. Create/Update/Delete) on every
    /// Metadata v2 publication of the named service so capability gates that read the v2
    /// graph (such as the FormPackage validator) treat the service as editable. Delegates
    /// to <see cref="Mixins.WebAppFixtureMetadataV2GraphMutationMixin"/>.
    /// </summary>
    public void EnableV2ServiceEditingCapabilities(string serviceName, IReadOnlyList<string> capabilities)
        => Honua.TestKit.Mixins.WebAppFixtureMetadataV2GraphMutationMixin.EnableServiceEditingCapabilities(
            this,
            serviceName,
            capabilities);

    /// <summary>
    /// Adds the Metadata v2 mirror for <c>tests/seed/admin-sample-feature-server.yaml</c>.
    /// Delegates to <see cref="Mixins.WebAppFixtureMetadataV2GraphMutationMixin"/>.
    /// </summary>
    public void AddAdminSampleMetadataV2Graph()
        => Honua.TestKit.Mixins.WebAppFixtureMetadataV2GraphMutationMixin.AddAdminSample(this);

    private async Task InitializeSharedAsync()
    {
        // Audit-A2: the process-wide shared-server lifecycle (semaphore + refcount +
        // factory/Postgres construction) lives on WebAppFixtureSharedBootstrapMixin.
        await Honua.TestKit.Mixins.WebAppFixtureSharedBootstrapMixin
            .EnsureInitializedAsync(SharedAdminPassword);

        if (_scopedServiceOverrides.Count > 0)
        {
            _serviceOverrideScopeId = Guid.NewGuid().ToString("N");
            Honua.TestKit.Mixins.WebAppFixtureSharedBootstrapMixin.Factory.Services
                .GetRequiredService<ScopedServiceOverrideRegistry>()
                .Add(_serviceOverrideScopeId, _scopedServiceOverrides);
        }

        if (string.IsNullOrWhiteSpace(_currentSchema))
        {
            _currentSchema = await Postgres.CreateIsolatedSchemaAsync(nameof(WebAppFixture));
        }

        await SeedSchemaAsync(_currentSchema);

        Client = CreateAdminClient();
        _serviceScope = Honua.TestKit.Mixins.WebAppFixtureSharedBootstrapMixin.Factory.Services.CreateScope();

        ApplySeedSpecificMetadataV2Graph();

        await EnsureTestSecureConnectionAsync();
    }

    public async Task DisposeAsync()
    {
        _serviceScope?.Dispose();
        _serviceScope = null;

        if (_useSharedServer)
        {
            if (_serviceOverrideScopeId is not null)
            {
                Honua.TestKit.Mixins.WebAppFixtureSharedBootstrapMixin.Factory.Services
                    .GetRequiredService<ScopedServiceOverrideRegistry>()
                    .Remove(_serviceOverrideScopeId);
                _serviceOverrideScopeId = null;
            }

            if (_currentSchema is not null)
            {
                await Postgres.DropSchemaAsync(_currentSchema);
            }

            Client.Dispose();

            // Audit-A2: ref-counted teardown of the shared factory + Postgres lives on
            // WebAppFixtureSharedBootstrapMixin.
            await Honua.TestKit.Mixins.WebAppFixtureSharedBootstrapMixin.ReleaseAsync();
            return;
        }

        if (_currentSchema is not null)
        {
            await Postgres.DropSchemaAsync(_currentSchema);
        }

        Client.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    /// <summary>
    /// Configure services before initialization (must be called before InitializeAsync).
    /// </summary>
    public WebAppFixture ConfigureServices(Action<IServiceCollection> configure)
    {
        _serviceConfigurations.Add(configure);
        return this;
    }

    /// <summary>
    /// Configure the web host before initialization.
    /// </summary>
    public WebAppFixture ConfigureWebHost(Action<IWebHostBuilder> configure)
    {
        _configureWebHost = _configureWebHost == null ? configure : _configureWebHost + configure;
        return this;
    }

    /// <summary>
    /// Configures the environment that both early <c>Program.cs</c> startup and the final
    /// web host observe. Must be called before <see cref="InitializeAsync"/>.
    /// </summary>
    public WebAppFixture UseEnvironment(string environmentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        _environmentName = environmentName;
        Action<IWebHostBuilder> configureEnvironment = builder => builder.UseEnvironment(environmentName);
        _configureWebHost = _configureWebHost == null
            ? configureEnvironment
            : _configureWebHost + configureEnvironment;
        return this;
    }

    /// <summary>
    /// Configure a seed file to apply when creating the test schema.
    /// Must be called before InitializeAsync.
    /// </summary>
    public WebAppFixture UseSeed(string seedPath, string? profile = null)
    {
        _seedPath = ResolveSeedPath(seedPath);
        _seedProfile = profile;
        return this;
    }

    /// <summary>
    /// Replace a service in the DI container with a test implementation.
    /// </summary>
    public WebAppFixture ReplaceService<TService, TImplementation>()
        where TService : class
        where TImplementation : class, TService
    {
        _serviceConfigurations.Add(services =>
        {
            services.RemoveAll<TService>();
            services.AddScoped<TService, TImplementation>();
        });
        return this;
    }

    /// <summary>
    /// Replace a service with a specific instance.
    /// </summary>
    public WebAppFixture ReplaceService<TService>(TService instance)
        where TService : class
    {
        _serviceConfigurations.Add(services =>
        {
            services.RemoveAll<TService>();
            services.AddSingleton(instance);
        });
        return this;
    }

    /// <summary>
    /// Replaces a request-resolved service for this fixture while reusing the shared
    /// application host. This opt-in is limited to concrete instances resolved directly
    /// from <c>HttpContext.RequestServices</c>; constructor-graph and hosted-service
    /// replacements must continue to use <see cref="ReplaceService{TService}(TService)"/>.
    /// </summary>
    public WebAppFixture ReplaceRequestService<TService>(TService instance)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        _scopedServiceOverrides[typeof(TService)] = instance;
        return this;
    }

    /// <summary>
    /// Get a service from the test server's DI container.
    /// </summary>
    public T GetService<T>() where T : notnull
    {
        if (_scopedServiceOverrides.TryGetValue(typeof(T), out var scopedOverride))
        {
            return (T)scopedOverride;
        }

        var provider = _serviceScope?.ServiceProvider
            ?? throw new InvalidOperationException("Service scope not initialized.");

        return provider.GetRequiredService<T>();
    }

    /// <summary>
    /// Get an optional service from the test server's DI container.
    /// </summary>
    public T? GetOptionalService<T>() where T : class
    {
        if (_scopedServiceOverrides.TryGetValue(typeof(T), out var scopedOverride))
        {
            return (T)scopedOverride;
        }

        return _serviceScope?.ServiceProvider.GetService<T>();
    }

    /// <summary>
    /// Get the test secure connection ID created by the fixture. Delegates to
    /// <see cref="Mixins.WebAppFixtureSecureConnectionMixin"/>.
    /// </summary>
    public Task<Guid?> GetTestSecureConnectionIdAsync()
        => Honua.TestKit.Mixins.WebAppFixtureSecureConnectionMixin.GetTestSecureConnectionIdAsync(_serviceScope);

    /// <summary>
    /// Create an isolated schema for this test.
    /// Schema is automatically cleaned up on dispose.
    /// </summary>
    public async Task<string> CreateIsolatedSchemaAsync(string testClassName)
    {
        if (!string.IsNullOrWhiteSpace(_currentSchema))
        {
            return _currentSchema;
        }

        _currentSchema = await Postgres.CreateIsolatedSchemaAsync(testClassName);
        await SeedSchemaAsync(_currentSchema);
        await EnsureTestSecureConnectionAsync();
        return _currentSchema;
    }

    /// <summary>
    /// Audit-A2 follow-up: the secure-connection bootstrap now lives on
    /// <see cref="Mixins.WebAppFixtureSecureConnectionMixin"/>. This wrapper preserves the call
    /// site shape that <c>InitializeAsync</c> / <c>CreateIsolatedSchemaAsync</c> use.
    /// </summary>
    private Task EnsureTestSecureConnectionAsync()
        => Honua.TestKit.Mixins.WebAppFixtureSecureConnectionMixin.EnsureTestSecureConnectionAsync(_serviceScope);

    private Task SeedSchemaAsync(string schemaName)
    {
        if (!string.IsNullOrWhiteSpace(_seedPath))
        {
            return Postgres.ApplySeedAsync(_seedPath, schemaName, _seedProfile);
        }

        return ServerTestData.SeedAsync(Postgres, schemaName);
    }

    private static string ResolveSeedPath(string seedPath)
    {
        if (Path.IsPathRooted(seedPath) || File.Exists(seedPath))
        {
            return seedPath;
        }

        // The Path.Combine calls below never risk dropping an earlier segment: "Honua.sln"
        // is a fixed literal, and seedPath is provably non-rooted here (the IsPathRooted
        // check above already returned for rooted paths).
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Join(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        if (directory != null)
        {
            var candidate = Path.Join(directory.FullName, seedPath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return seedPath;
    }


    /// <summary>
    /// Reset database state in the public schema (legacy method).
    /// Prefer schema-based isolation for parallel execution.
    /// </summary>
    public async Task ResetAsync()
    {
        await Postgres.ResetAsync();
    }

    /// <summary>
    /// Creates an <see cref="HttpMessageHandler"/> connected to the in-memory test server.
    /// Useful for constructing custom transports (e.g. gRPC channels) that route
    /// through the same pipeline as <see cref="Client"/>.
    /// </summary>
    public HttpMessageHandler CreateHandler()
    {
        var handler = ActiveFactory.Server.CreateHandler();
        return _serviceOverrideScopeId is null
            ? handler
            : new ServiceOverrideScopeHandler(_serviceOverrideScopeId, handler);
    }

    /// <summary>
    /// Creates a <see cref="Microsoft.AspNetCore.TestHost.WebSocketClient"/> for testing
    /// WebSocket endpoints through the in-memory test server.
    /// </summary>
    public Microsoft.AspNetCore.TestHost.WebSocketClient CreateWebSocketClient()
    {
        var client = ActiveFactory.Server.CreateWebSocketClient();
        if (_serviceOverrideScopeId is not null)
        {
            client.ConfigureRequest = request =>
                request.Headers[ScopedServiceOverrideRegistry.HeaderName] = _serviceOverrideScopeId;
        }

        return client;
    }

    /// <summary>
    /// Create a new HTTP client with custom configuration.
    /// </summary>
    public HttpClient CreateClient(Action<HttpClient>? configure = null)
    {
        var client = ActiveFactory.CreateClient();
        client.Timeout = _defaultTestClientTimeout;
        ApplyCurrentSchemaHeader(client);
        configure?.Invoke(client);
        return client;
    }

    /// <summary>
    /// Create a new HTTP client with control over automatic redirect following.
    /// Pass <see langword="false"/> to inspect 3xx responses (their <c>Location</c>
    /// header) instead of transparently following them — required when a redirect
    /// target points off-server (for example the OAuth2 bridge IdP leg).
    /// </summary>
    public HttpClient CreateClient(bool allowAutoRedirect)
    {
        var client = ActiveFactory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = allowAutoRedirect,
        });
        client.Timeout = _defaultTestClientTimeout;
        ApplyCurrentSchemaHeader(client);

        return client;
    }

    /// <summary>
    /// Create a new HTTP client with admin authorization for testing admin endpoints.
    /// </summary>
    public HttpClient CreateAdminClient()
    {
        return CreateClient(client =>
        {
            client.DefaultRequestHeaders.Add("X-API-Key", SharedAdminPassword);
        });
    }

    /// <summary>
    /// Create a new HTTP client scoped to a specific database schema.
    /// </summary>
    public HttpClient CreateClient(string schemaName)
    {
        if (!string.IsNullOrWhiteSpace(schemaName))
        {
            _currentSchema = schemaName;
        }

        var client = CreateClient();

        return client;
    }

    private void ApplyCurrentSchemaHeader(HttpClient client)
    {
        if (_serviceOverrideScopeId is not null)
        {
            client.DefaultRequestHeaders.Remove(ScopedServiceOverrideRegistry.HeaderName);
            client.DefaultRequestHeaders.Add(
                ScopedServiceOverrideRegistry.HeaderName,
                _serviceOverrideScopeId);
        }

        if (string.IsNullOrWhiteSpace(_currentSchema))
        {
            return;
        }

        client.DefaultRequestHeaders.Remove("X-Honua-Test-Schema");
        client.DefaultRequestHeaders.Add("X-Honua-Test-Schema", _currentSchema);
    }

    private sealed class ServiceOverrideScopeHandler(
        string scopeId,
        HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Headers.Remove(ScopedServiceOverrideRegistry.HeaderName);
            request.Headers.Add(ScopedServiceOverrideRegistry.HeaderName, scopeId);
            return base.SendAsync(request, cancellationToken);
        }
    }

}
