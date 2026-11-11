// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Server.Tests.Infrastructure;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

// One schema of control-plane state tables for tests that need real stores but own no database
// fixture. Records are keyed by fresh identifiers, so tests do not see each other's rows.
internal static class SharedControlPlaneState
{
    private static readonly Lazy<Task<ControlPlaneStateHarness>> _harness = new(CreateAsync);

    public static Task<ControlPlaneStateHarness> GetAsync() => _harness.Value;

    private static async Task<ControlPlaneStateHarness> CreateAsync()
    {
        var database = new DatabaseFixtureAdapter();
        await database.InitializeAsync();
        return await ControlPlaneStateHarness.CreateAsync(database, nameof(SharedControlPlaneState));
    }
}
