// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Startup;

// An in-process test host skips the real providers, and a standalone test image that exposes the fixture must register them.
internal static class TestInfrastructureRegistrationPolicy
{
    // A host that skips migrations must not opt into the real providers.
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
