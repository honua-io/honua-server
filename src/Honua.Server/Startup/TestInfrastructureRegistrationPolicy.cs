// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Startup;

/// <summary>
/// Decides whether the data-provider infrastructure composition root
/// (<see cref="InfrastructureCompositionRoot.RegisterInfrastructureServices"/>) should run in
/// the Test environment. Production and Development always register it; in Test it is skipped
/// by default so in-process <c>WebAppFixture</c> hosts can substitute their own isolated
/// providers without double-registration.
/// </summary>
/// <remarks>
/// Standalone Test images (for example the Docker image the honua-console live lane boots via
/// Testcontainers) run in the Test environment only to satisfy the Operate observability
/// fixture validator, but they have no in-process fixture to supply providers. When such an
/// image exposes the fixture seed endpoint it must register the real providers the seeder
/// resolves (<c>IAlertAdminStore</c>, <c>IAlertEventStore</c>, <c>IAlertLifecycleStore</c>,
/// <c>IDatabaseConnectionProvider</c>, and the investigation/audit stores); otherwise
/// <c>POST /api/v1/admin/dev/fixtures/operate-observability/{profile}</c> throws
/// "No service for type 'IAlertAdminStore' has been registered." and returns 500
/// (honua-server#2350).
/// </remarks>
internal static class TestInfrastructureRegistrationPolicy
{
    /// <summary>
    /// Determines whether the infrastructure composition root should register the configured
    /// data provider for the current host.
    /// </summary>
    /// <param name="isTestEnvironment">Whether the host runs in the Test environment.</param>
    /// <param name="explicitTestOptIn">
    /// Whether <c>HONUA_REGISTER_TEST_INFRASTRUCTURE</c> opted the Test host back in.
    /// </param>
    /// <param name="operateObservabilityFixtureEnabled">
    /// Whether the Operate observability seed fixture endpoint is enabled.
    /// </param>
    /// <param name="hostManagesOwnMigrations">
    /// Whether this host runs its own database migrations. Standalone deployments manage their
    /// own migrations; in-process WebAppFixture hosts set <c>HONUA_SKIP_MIGRATIONS=true</c> and
    /// wire their own isolated providers, so they must not trigger this opt-in.
    /// </param>
    /// <returns><see langword="true"/> when infrastructure services should be registered.</returns>
    public static bool ShouldRegisterInfrastructure(
        bool isTestEnvironment,
        bool explicitTestOptIn,
        bool operateObservabilityFixtureEnabled,
        bool hostManagesOwnMigrations)
    {
        if (!isTestEnvironment)
        {
            return true;
        }

        if (explicitTestOptIn)
        {
            return true;
        }

        // A self-migrating standalone Test image that exposes the Operate observability fixture
        // endpoint needs the real providers the seeder resolves. WebAppFixture hosts skip
        // migrations and register their own providers, so they are intentionally excluded to
        // avoid double-registration.
        return operateObservabilityFixtureEnabled && hostManagesOwnMigrations;
    }
}
