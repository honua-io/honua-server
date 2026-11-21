// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.TestKit;

namespace Honua.Server.Tests.Features.Capabilities;

public sealed partial class CapabilityManifestEndpointTests
{
    // Applications that differ from the default one only by the manifest source or the
    // experimental switch. Each is started on first use and shared by every case that asks
    // for the same configuration; those cases only read the manifest. Cases with any other
    // configuration still build and dispose their own application.
    public sealed class ConfiguredApplications : IAsyncLifetime
    {
        private readonly Dictionary<ManifestConfiguration, WebAppFixture> _applications = [];

        public async Task<WebAppFixture> GetAsync(ManifestConfiguration configuration)
        {
            if (_applications.TryGetValue(configuration, out var started))
            {
                return started;
            }

            var fixture = configuration.CreateFixture();
            try
            {
                await fixture.InitializeAsync();
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }

            _applications.Add(configuration, fixture);
            return fixture;
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            foreach (var fixture in _applications.Values)
            {
                await fixture.DisposeAsync();
            }

            _applications.Clear();
        }
    }

    public sealed record ManifestConfiguration
    {
        public bool FromRegistry { get; init; }

        public bool ExperimentalEnabled { get; init; } = true;

        internal WebAppFixture CreateFixture() => CreateManifestFixture(
            manifestFromRegistry: FromRegistry,
            experimentalGlobalEnabled: ExperimentalEnabled);
    }
}
