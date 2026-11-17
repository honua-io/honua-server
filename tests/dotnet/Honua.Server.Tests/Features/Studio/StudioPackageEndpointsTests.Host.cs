// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Publishing.Content.Abstractions;
using Honua.Core.Features.Publishing.Content.Services;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Services;
using Honua.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Studio;

public sealed partial class StudioPackageEndpointsTests
{
    // One host per class. Each case creates drafts and items under its own package keys and reads
    // them back by id, so cases share the in-memory stores; the audit log is cleared at the start
    // of each case.
    public sealed class StudioHost : IAsyncLifetime
    {
        internal CapturingAuditLog AuditLog { get; } = new();

        internal WebAppFixture Fixture { get; }

        internal HttpClient Client { get; private set; } = null!;

        public StudioHost()
        {
            Fixture = new WebAppFixture()
                .ConfigureWebHost(builder =>
                {
                    builder.UseEnvironment("Test");
                    builder.UseSetting("HONUA_DEV_AUTH", "false");
                    builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
                })
                .ConfigureServices(services =>
                {
                    services.RemoveAll<IStudioPackageStore>();
                    services.AddSingleton<IStudioPackageStore, InMemoryStudioPackageStore>();
                    // The content-items list endpoint joins publication-registry lifecycle badges
                    // (REQ-004); the in-memory store exercises the HTTP and join path without a
                    // migrated Postgres schema. PostgresContentPublicationStoreTests covers the
                    // Postgres store's join query.
                    services.RemoveAll<IContentPublicationStore>();
                    services.AddSingleton<IContentPublicationStore, InMemoryContentPublicationStore>();
                    services.RemoveAll<IAuditLog>();
                    services.AddSingleton<IAuditLog>(AuditLog);
                });
        }

        public async Task InitializeAsync()
        {
            await Fixture.InitializeAsync();
            Client = Fixture.CreateAdminClient();
        }

        public async Task DisposeAsync()
        {
            Client?.Dispose();
            await Fixture.DisposeAsync();
        }
    }
}
