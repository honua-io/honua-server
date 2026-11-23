// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Capabilities;

public sealed partial class DeploymentCapabilityProfileIntegrationTests
{
    // One application per configured capability for the folder-path cases. Each is started
    // on first use with the test service moved into the "Public" folder as "Parcels", and is
    // then shared by every folder-path case that configures the same capability; those cases
    // only send GET requests. Other cases build and dispose their own application.
    public sealed class FolderApplications : IAsyncLifetime
    {
        private readonly Dictionary<string, WebAppFixture> _applications = new(StringComparer.Ordinal);
        private WebAppFixture? _schemaOwner;

        public async Task<WebAppFixture> GetAsync(string capability)
        {
            if (_applications.TryGetValue(capability, out var started))
            {
                return started;
            }

            var fixture = CreateFixture([capability]);
            if (_schemaOwner is not null)
            {
                fixture.ShareSchemaWith(_schemaOwner);
            }

            try
            {
                await fixture.InitializeAsync();
                MoveTestServiceIntoPublicFolder(fixture);
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }

            _applications.Add(capability, fixture);
            _schemaOwner ??= fixture;
            return fixture;
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            // Stop schema borrowers before disposing the first application, which owns it.
            foreach (var fixture in _applications.Values.Reverse())
            {
                await fixture.DisposeAsync();
            }

            _applications.Clear();
            _schemaOwner = null;
        }

        private static void MoveTestServiceIntoPublicFolder(WebAppFixture fixture)
        {
            var original = fixture.GetCurrentV2GraphSnapshot().Graph;
            var provider = (TestMetadataV2GraphProvider)fixture.GetService<IMetadataV2GraphProvider>();
            provider.SetGraph(original with
            {
                Folders = [new() { Metadata = new() { Id = "folder", Name = "Public" } }],
                Services = original.Services.Select(service => service.Metadata.Name == WebAppFixture.TestServiceId
                    ? service with { FolderId = "folder", PublicName = "Parcels" } : service).ToArray()
            }, schema: fixture.CurrentSchema);
        }
    }
}
