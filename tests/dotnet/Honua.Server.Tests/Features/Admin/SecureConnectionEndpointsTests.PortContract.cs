// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security.Abstractions;
using Honua.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

public partial class SecureConnectionEndpointsTests : IClassFixture<SecureConnectionEndpointsTests.PortContractHost>
{
    // One host for every port-contract case. The cases differ only in their request, and each
    // creates its connection under a unique name, so they share the allowlist, the secret resolver
    // and the capturing driver; the driver is reset on every borrow so a case sees only its own
    // probe. Started on first use so filtered runs that skip the theory do not boot it.
    public sealed class PortContractHost : IAsyncLifetime
    {
        private readonly WebAppFixture _application;
        private readonly Lazy<Task> _startup;

        public PortContractHost()
        {
            var secrets = Substitute.For<IRequestSecretReferenceResolver>();
            secrets.Evaluate(Arg.Any<string>()).Returns(RequestSecretReferenceDecision.Permitted());
            secrets.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns("Host=localhost;Port=6432;Database=test;Username=test;Password=test;SSL Mode=Require");
            _application = CreateAllowlistFixture(["localhost"], false, out _)
                .ConfigureServices(services =>
                {
                    services.RemoveAll<IRequestSecretReferenceResolver>();
                    services.AddSingleton(secrets);
                    services.RemoveAll<IConnectionDriverRegistry>();
                    services.AddSingleton<IConnectionDriverRegistry>(new ConnectionDriverRegistry([Driver]));
                });
            _startup = new Lazy<Task>(_application.InitializeAsync);
        }

        internal CapturingConnectionDriver Driver { get; } = new();

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task<WebAppFixture> BorrowAsync()
        {
            await _startup.Value;
            Driver.Reset();
            return _application;
        }

        public Task DisposeAsync() => _application.DisposeAsync();
    }
}
