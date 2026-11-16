// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

internal static class StudioMcpFixtures
{
    // Composition tools use Pro; draft mutation approval is exercised by separate suites.
    internal static WebAppFixture CreateCompositionFixture() => new WebAppFixture()
        .ConfigureWebHost(builder => builder.UseSetting("Licensing:DevGrantEdition", "Pro"))
        .ConfigureServices(services =>
        {
            services.RemoveAll<IStudioPackageStore>();
            services.AddSingleton<IStudioPackageStore, InMemoryStudioPackageStore>();
        });

    // Setup must not invoke approval-gated draft creation or update tools.
    internal static async Task<StudioPackageDraft> SeedMapDraftAsync(
        WebAppFixture fixture, string packageKey, JsonElement? body = null)
    {
        using var scope = fixture.Services.CreateScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<IStudioPackageLifecycleService>();
        var format = lifecycle.GetCapabilities().Families.Single(family => family.Family == StudioPackageFamily.Map).Format;
        return await lifecycle.CreateDraftAsync(new CreateStudioPackageDraftCommand
        {
            PackageKey = packageKey,
            OwnerId = WebAppFixture.DevelopmentBypassActorId,
            ActorId = WebAppFixture.DevelopmentBypassActorId,
            Envelope = new StudioPackageEnvelope
            {
                Family = StudioPackageFamily.Map,
                SchemaVersion = "1.0",
                Format = format,
                Body = body,
            },
        });
    }
}
