// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.TestKit;

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
    private readonly Dictionary<string, string?> _configurationOverrides = new(StringComparer.OrdinalIgnoreCase);
    private WebApplicationFactory<Program>? _factory;
    private string? _currentSchema;
    private string _environmentName = "Test";
    private bool _useSharedServer;
    private bool _hasSharedReference;
    private int _disposeState;
    private bool _useKestrel;
    private Action<KestrelServerOptions>? _configureKestrelListeners;
    private string? _seedPath;
    private string? _seedProfile;
    private IServiceScope? _serviceScope;
    private string? _serviceOverrideScopeId;
    private WebAppFixture? _schemaOwner;

    public const string TestServiceId = "test";

    public const int TestLayerId = 0;

    // Audit-A2: the geocoding base URL and encryption material constants used to live
    // here as static strings. They now live on
    // <see cref="WebAppFixturePostgresWiringMixin"/> next to the configuration-building
    // helpers that read them.

    // Audit-A2: the default GeoServices drawingInfo, V2 graph factories, and per-layer
    // schema/spatial/temporal helpers historically lived inline as ~770 LOC of static
    // helpers. They are pure functions over layer ids and seed-file names — no fixture
    // state — and now live on Honua.TestKit.Mixins.WebAppFixtureMetadataV2Mixin.

    public const string SharedAdminPassword = "test-admin-password";

    public const string SharedAdminActorId = "00000000-0000-0000-0000-000000000002";

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

    public IDatabaseConnectionProvider DatabaseConnectionProvider => GetService<IDatabaseConnectionProvider>();

    public IServiceProvider Services => ActiveFactory.Services;

    private bool HasCustomConfiguration => _useKestrel || _serviceConfigurations.Count > 0
        || _configureWebHost != null
        || _configurationOverrides.Count > 0
        || !string.Equals(_environmentName, "Test", StringComparison.OrdinalIgnoreCase);

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
        await InitializeIsolatedHostAsync();
    }

    public async Task RestartHostAsync()
    {
        if (_useSharedServer || _factory is null || _postgres is null)
        {
            throw new InvalidOperationException("Restart requires an initialized, isolated host.");
        }
        _serviceScope?.Dispose();
        _serviceScope = null;
        Client.Dispose();
        await _factory.DisposeAsync();
        _factory = null;
        await InitializeIsolatedHostAsync();
    }

    private async Task InitializeIsolatedHostAsync()
    {
        var postgres = _postgres ?? throw new InvalidOperationException("Postgres fixture is not initialized.");

        // Create and seed the fixture schema before the host is built so the host can
        // configure it as an operational schema (#5391). Seeding touches only the database.
        if (_schemaOwner is not null)
        {
            _currentSchema = _schemaOwner.CurrentSchema
                ?? throw new InvalidOperationException("The schema owner must be initialized before a host shares its schema.");
        }

        if (string.IsNullOrWhiteSpace(_currentSchema))
        {
            _currentSchema = await postgres.CreateIsolatedSchemaAsync(nameof(WebAppFixture));
            await SeedSchemaAsync(_currentSchema);
        }

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
                            .BuildAppConfigurationDictionary(
                                postgres.ConnectionString,
                                Honua.TestKit.Mixins.WebAppFixturePostgresWiringMixin
                                    .BuildFixtureOperationalSchemaSettings(_currentSchema)));
                    configBuilder.AddInMemoryCollection(_configurationOverrides);
                });

                builder.ConfigureTestServices(services =>
                {
                    Honua.TestKit.Mixins.WebAppFixturePostgresWiringMixin.ConfigureIsolatedTestServices(
                        services,
                        postgres.ConnectionString,
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

        if (_useKestrel)
        {
            if (_configureKestrelListeners is null)
            {
                _factory.UseKestrel(0);
            }
            else
            {
                _factory.UseKestrel(_configureKestrelListeners);
            }
        }

        Client = CreateClient();
        _serviceScope = _factory.Services.CreateScope();

        ApplyCurrentSchemaHeader(Client);

        ApplySeedSpecificMetadataV2Graph();

        await EnsureTestSecureConnectionAsync();
    }

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

    public void UpdateV2ResourceMetadata(
        int layerIndex,
        AccessPolicy? accessPolicy = null,
        MetadataV2ObjectTemporal? temporal = null,
        MetadataV2ObjectSpatial? spatial = null,
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

    public MetadataV2GraphSnapshot GetCurrentV2GraphSnapshot()
        => Honua.TestKit.Mixins.WebAppFixtureMetadataV2GraphMutationMixin.GetCurrentSnapshot(this);

    public void MutateV2Graph(Func<MetadataV2Graph, MetadataV2Graph> mutate)
        => Honua.TestKit.Mixins.WebAppFixtureMetadataV2GraphMutationMixin.MutateGraph(this, mutate);

    public void MutateV2ResourceObjectMetadata(
        int layerIndex,
        Func<MetadataV2ObjectMetadata, MetadataV2ObjectMetadata> mutate)
        => Honua.TestKit.Mixins.WebAppFixtureMetadataV2GraphMutationMixin.MutateResourceObjectMetadata(
            this,
            layerIndex,
            mutate);

    public void UpdateV2ResourceSchemaField(int layerIndex, MetadataV2Field field)
        => Honua.TestKit.Mixins.WebAppFixtureMetadataV2GraphMutationMixin.UpdateResourceSchemaField(this, layerIndex, field);

    public void RestoreV2ResourceSchemaFields(int layerIndex, IReadOnlyList<MetadataV2Field> fields)
        => Honua.TestKit.Mixins.WebAppFixtureMetadataV2GraphMutationMixin.RestoreResourceSchemaFields(this, layerIndex, fields);

    public void UpdateV2ResourceEditing(int layerIndex, MetadataV2ObjectEditing? editing)
        => Honua.TestKit.Mixins.WebAppFixtureMetadataV2GraphMutationMixin.UpdateResourceEditing(this, layerIndex, editing);

    public void UpdateV2ResourceSubtypes(int layerIndex, MetadataV2Subtypes? subtypes)
        => Honua.TestKit.Mixins.WebAppFixtureMetadataV2GraphMutationMixin.UpdateResourceSubtypes(this, layerIndex, subtypes);

    // Replaces the resource published at layerIndex. Delegates to
    // Mixins.WebAppFixtureMetadataV2GraphMutationMixin.
    public Task UpdateV2ResourceAsync(int layerIndex, Func<MetadataV2Object, MetadataV2Object> mutate)
        => Honua.TestKit.Mixins.WebAppFixtureMetadataV2GraphMutationMixin.UpdateResourceAsync(this, layerIndex, mutate);

    public Task UpdateV2ResourceAttributeRulesAsync(
        int layerIndex,
        IReadOnlyList<MetadataV2AttributeRule>? attributeRules,
        CancellationToken cancellationToken = default)
        => Honua.TestKit.Mixins.WebAppFixtureMetadataV2GraphMutationMixin.UpdateResourceAttributeRulesAsync(
            this,
            layerIndex,
            attributeRules,
            cancellationToken);

    public Task UpdateV2ResourceContingentValueGroupsAsync(
        int layerIndex,
        IReadOnlyList<MetadataV2ContingentValueGroup>? contingentValueGroups,
        CancellationToken cancellationToken = default)
        => Honua.TestKit.Mixins.WebAppFixtureMetadataV2GraphMutationMixin.UpdateResourceContingentValueGroupsAsync(
            this,
            layerIndex,
            contingentValueGroups,
            cancellationToken);

    public void UpdateV2ResourceName(int layerIndex, string newName)
        => Honua.TestKit.Mixins.WebAppFixtureMetadataV2GraphMutationMixin.UpdateResourceName(this, layerIndex, newName);

    public void UpdateV2ServiceMetadata(
        string serviceName,
        IReadOnlyList<string>? enabledProtocols = null,
        AccessPolicy? accessPolicy = null,
        bool clearAccessPolicy = false,
        IReadOnlyList<string>? capabilities = null)
        => Honua.TestKit.Mixins.WebAppFixtureMetadataV2GraphMutationMixin.UpdateServiceMetadata(
            this,
            serviceName,
            enabledProtocols,
            accessPolicy,
            clearAccessPolicy,
            capabilities);

    public void SetV2LayerEnabled(int layerIndex, bool enabled)
        => Honua.TestKit.Mixins.WebAppFixtureMetadataV2GraphMutationMixin.SetLayerEnabled(this, layerIndex, enabled);

    public void EnableV2ServiceEditingCapabilities(string serviceName, IReadOnlyList<string> capabilities)
        => Honua.TestKit.Mixins.WebAppFixtureMetadataV2GraphMutationMixin.EnableServiceEditingCapabilities(
            this,
            serviceName,
            capabilities);

    public void AddAdminSampleMetadataV2Graph()
        => Honua.TestKit.Mixins.WebAppFixtureMetadataV2GraphMutationMixin.AddAdminSample(this);

    private async Task InitializeSharedAsync()
    {
        // Audit-A2: the process-wide shared-server lifecycle (semaphore + refcount +
        // factory/Postgres construction) lives on WebAppFixtureSharedBootstrapMixin.
        await Honua.TestKit.Mixins.WebAppFixtureSharedBootstrapMixin
            .EnsureInitializedAsync(SharedAdminPassword);
        _hasSharedReference = true;

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
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }
        _serviceScope?.Dispose();
        _serviceScope = null;

        if (_useSharedServer)
        {
            await DisposeSharedServerAsync();
        }
        else
        {
            await DisposePrivateServerAsync();
        }
    }

    private async Task DisposeSharedServerAsync()
    {
        if (!_hasSharedReference)
        {
            return;
        }
        try
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
        }
        finally
        {
            Client?.Dispose();
            _hasSharedReference = false;
            await Honua.TestKit.Mixins.WebAppFixtureSharedBootstrapMixin.ReleaseAsync();
        }
    }

    private async Task DisposePrivateServerAsync()
    {
        try
        {
            if (_currentSchema is not null && _postgres is not null && _schemaOwner is null)
            {
                await _postgres.DropSchemaAsync(_currentSchema);
            }
        }
        finally
        {
            Client?.Dispose();
            try
            {
                if (_factory is not null)
                {
                    await _factory.DisposeAsync();
                }
            }
            finally
            {
                if (_postgres is not null)
                {
                    await _postgres.DisposeAsync();
                }
            }
        }
    }

    public WebAppFixture ConfigureServices(Action<IServiceCollection> configure)
    {
        _serviceConfigurations.Add(configure);
        return this;
    }

    // Settings applied after the fixture's own, for a test that must replace one the fixture sets.
    public WebAppFixture OverrideConfiguration(IReadOnlyDictionary<string, string?> settings)
    {
        foreach (var setting in settings)
        {
            _configurationOverrides[setting.Key] = setting.Value;
        }

        return this;
    }

    public WebAppFixture ConfigureWebHost(Action<IWebHostBuilder> configure)
    {
        _configureWebHost = _configureWebHost == null ? configure : _configureWebHost + configure;
        return this;
    }

    public WebAppFixture UseKestrel(Action<KestrelServerOptions>? configureListeners = null)
    {
        if (_factory is not null || _useSharedServer)
        {
            throw new InvalidOperationException("Configure Kestrel before initializing the fixture.");
        }

        _useKestrel = true;
        _configureKestrelListeners = configureListeners;
        return this;
    }

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

    public WebAppFixture ShareSchemaWith(WebAppFixture owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _schemaOwner = owner;
        return this;
    }

    public WebAppFixture UseSeed(string seedPath, string? profile = null)
    {
        _seedPath = ResolveSeedPath(seedPath);
        _seedProfile = profile;
        return this;
    }

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

    public WebAppFixture DecorateService<TService>(Func<TService, TService> decorate)
        where TService : class
        => DecorateRegistrations(decorate, static registrations => registrations.TakeLast(1));

    public WebAppFixture DecorateEachService<TService>(Func<TService, TService> decorate)
        where TService : class
        => DecorateRegistrations(decorate, static registrations => registrations);

    private WebAppFixture DecorateRegistrations<TService>(
        Func<TService, TService> decorate,
        Func<ServiceDescriptor[], IEnumerable<ServiceDescriptor>> selectRegistrations)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(decorate);
        _serviceConfigurations.Add(services =>
        {
            var registrations = services.Where(descriptor => descriptor.ServiceType == typeof(TService)).ToArray();
            if (registrations.Length == 0)
            {
                throw new InvalidOperationException($"No registration for {typeof(TService).Name} to decorate.");
            }
            foreach (var original in selectRegistrations(registrations))
            {
                var index = services.IndexOf(original);
                services[index] = ServiceDescriptor.Describe(
                    typeof(TService),
                    provider => decorate((TService)ResolveOriginal(provider, original)),
                    original.Lifetime);
            }
        });
        return this;
    }

    private static object ResolveOriginal(IServiceProvider provider, ServiceDescriptor original)
    {
        if (original.ImplementationInstance is { } instance)
        {
            return instance;
        }

        if (original.ImplementationFactory is { } factory)
        {
            return factory(provider);
        }

        return ActivatorUtilities.CreateInstance(
            provider,
            original.ImplementationType
                ?? throw new InvalidOperationException(
                    $"Cannot decorate {original.ServiceType.Name}: the registration has no implementation."));
    }

    public WebAppFixture ReplaceRequestService<TService>(TService instance)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        _scopedServiceOverrides[typeof(TService)] = instance;
        return this;
    }

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

    public T? GetOptionalService<T>() where T : class
    {
        if (_scopedServiceOverrides.TryGetValue(typeof(T), out var scopedOverride))
        {
            return (T)scopedOverride;
        }

        return _serviceScope?.ServiceProvider.GetService<T>();
    }

    public Task<Guid?> GetTestSecureConnectionIdAsync()
        => Honua.TestKit.Mixins.WebAppFixtureSecureConnectionMixin.GetTestSecureConnectionIdAsync(_serviceScope);

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

    public Task EnsureTestSecureConnectionAsync()
        => Honua.TestKit.Mixins.WebAppFixtureSecureConnectionMixin.EnsureTestSecureConnectionAsync(_serviceScope);

    private async Task SeedSchemaAsync(string schemaName)
    {
        if (!string.IsNullOrWhiteSpace(_seedPath))
        {
            await Postgres.ApplySeedAsync(_seedPath, schemaName, _seedProfile).ConfigureAwait(false);
        }
        else
        {
            await ServerTestData.SeedAsync(Postgres, schemaName).ConfigureAwait(false);
        }

        await ServerTestData.EnsureFixtureAttachmentsAsync(Postgres, schemaName).ConfigureAwait(false);
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


    public async Task ResetAsync()
    {
        await Postgres.ResetAsync();
    }

    public HttpMessageHandler CreateHandler()
    {
        var handler = ActiveFactory.Server.CreateHandler();
        return _serviceOverrideScopeId is null
            ? handler
            : new ServiceOverrideScopeHandler(_serviceOverrideScopeId, handler);
    }

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

    public HttpClient CreateClient(Action<HttpClient>? configure = null)
    {
        var client = ActiveFactory.CreateClient();
        UseHttpListener(client);
        client.Timeout = _defaultTestClientTimeout;
        ApplyCurrentSchemaHeader(client);
        configure?.Invoke(client);
        return client;
    }

    public HttpClient CreateClient(bool allowAutoRedirect)
    {
        var client = ActiveFactory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = allowAutoRedirect,
        });
        UseHttpListener(client);
        client.Timeout = _defaultTestClientTimeout;
        ApplyCurrentSchemaHeader(client);

        return client;
    }

    public HttpClient CreateAdminClient()
    {
        return CreateClient(client =>
        {
            client.DefaultRequestHeaders.Add("X-API-Key", SharedAdminPassword);
        });
    }

    public HttpClient CreateClient(string schemaName)
    {
        if (!string.IsNullOrWhiteSpace(schemaName))
        {
            _currentSchema = schemaName;
        }

        var client = CreateClient();

        return client;
    }

    // Custom listeners must include one plain HTTP listener; the fixture's clients always use it.
    private void UseHttpListener(HttpClient client)
    {
        if (_configureKestrelListeners is null)
        {
            return;
        }

        var addresses = Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("The Kestrel test server did not report its listener addresses.");
        var httpAddress = addresses.First(address => address.StartsWith(Uri.UriSchemeHttp + "://", StringComparison.Ordinal));
        client.BaseAddress = new Uri(httpAddress);
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
