// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.TestKit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class ProposalEndpointsTests
{
    // One host serves every case. Each case starts with the guardrail tier, executor and notifier
    // counts and identity-provider token of a fresh host; proposals and operation instances
    // persist, and every case seeds and reads its own ids.
    public sealed class ProposalHost : IAsyncLifetime
    {
        private const string ClientAddressHeader = "X-Proposal-Test-Client";
        private HttpClient? _client;

        public ProposalHost()
        {
            Fixture = HumanApprovalHttpClient.Configure(new WebAppFixture(), IdentityProvider)
                .ConfigureWebHost(builder =>
                {
                    builder.UseEnvironment("Test");
                    builder.UseSetting("HONUA_DEV_AUTH", "false");
                    builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                })
                .ConfigureServices(ReplaceOperationServices);
        }

        internal TestProposalStore ProposalStore { get; } = new();

        internal StubGuardrailLadder Ladder { get; } = new();

        internal RecordingProposalNotifier Notifier { get; } = new();

        internal RecordingExecutor Executor { get; } = new();

        internal AdminAuthEndpointsTests.StubHttpClientFactory IdentityProvider { get; } = AdminAuthEndpointsTests.CreateStubFactory();

        internal WebAppFixture Fixture { get; }

        internal HttpClient Client => _client ?? throw new InvalidOperationException("The proposal host has not started.");

        public async Task InitializeAsync()
        {
            await Fixture.InitializeAsync();
            _client = Fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
        }

        public async Task DisposeAsync()
        {
            using var identityProvider = IdentityProvider;
            _client?.Dispose();
            await Fixture.DisposeAsync();
        }

        internal void ResetForCase()
        {
            Ladder.Tier = GuardrailTier.RequiresApproval;
            Executor.Reset();
            Notifier.Reset();
            ProposalStore.AdvanceVersionOnNextClaim = false;
            IdentityProvider.IdTokenOverride = null;
            IdentityProvider.LastTokenFormValues.Clear();
        }

        // The same client a fresh host's sign-in helper creates, with an address of its own.
        internal HttpClient CreateBrowserClient()
        {
            var client = Fixture.CreateClient();
            client.BaseAddress = new Uri("https://localhost");
            client.DefaultRequestHeaders.Add(ClientAddressHeader, Guid.NewGuid().ToString("N"));
            return client;
        }

        private void ReplaceOperationServices(IServiceCollection services)
        {
            services.RemoveAll<IOperationProposalStore>();
            services.RemoveAll<IGuardrailLadder>();
            services.RemoveAll<IProposalNotifier>();
            services.RemoveAll<IOperationGateway>();
            services.RemoveAll<IOperationExecutor>();
            services.RemoveAll<IOperationExecutorCatalog>();

            services.AddSingleton<IOperationProposalStore>(ProposalStore);
            services.AddSingleton<IGuardrailLadder>(Ladder);
            services.AddSingleton<IProposalNotifier>(Notifier);
            services.AddSingleton<IOperationExecutor>(Executor);
            services.AddSingleton<IOperationExecutorCatalog>(
                new TestExecutorCatalog([OperationClass.AdminConfigChange]));
            services.AddSingleton<IOperationGateway, Honua.ControlPlane.OperationGateway>();
            // Registered after the sign-in helper's fixed per-host address, so this one wins.
            services.AddSingleton<IStartupFilter>(new PerClientAddress());
        }

        private sealed class PerClientAddress : IStartupFilter
        {
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => application =>
            {
                application.Use((HttpContext context, RequestDelegate continueRequest) =>
                {
                    if (Guid.TryParse(context.Request.Headers[ClientAddressHeader], out var client))
                    {
                        context.Connection.RemoteIpAddress = new IPAddress(client.ToByteArray());
                    }

                    return continueRequest(context);
                });
                next(application);
            };
        }
    }

    internal sealed class StubGuardrailLadder : IGuardrailLadder
    {
        public GuardrailTier Tier { get; set; } = GuardrailTier.RequiresApproval;

        public GuardrailDecision Resolve(OperationClass operationClass)
            => Resolve(operationClass, Honua.Core.Features.Licensing.Domain.HonuaEdition.Enterprise);

        public GuardrailDecision Resolve(
            OperationClass operationClass,
            Honua.Core.Features.Licensing.Domain.HonuaEdition edition)
            => new(Tier, operationClass, edition, "test-stub");

        public GuardrailDecision Resolve(OperationClass operationClass, string? actionDiscriminator)
            => Resolve(operationClass, actionDiscriminator, Honua.Core.Features.Licensing.Domain.HonuaEdition.Enterprise);

        public GuardrailDecision Resolve(
            OperationClass operationClass,
            string? actionDiscriminator,
            Honua.Core.Features.Licensing.Domain.HonuaEdition edition)
            => new(Tier, operationClass, edition, "test-stub");
    }

    internal sealed class TestExecutorCatalog(IReadOnlyCollection<OperationClass> supportedKinds)
        : IOperationExecutorCatalog
    {
        public IReadOnlyCollection<OperationClass> SupportedKinds { get; } = supportedKinds;
    }

    internal sealed class RecordingExecutor : IOperationExecutor
    {
        public bool Executed { get; private set; }

        public int ExecutionCount { get; private set; }

        public void Reset()
        {
            Executed = false;
            ExecutionCount = 0;
        }

        public OperationClass OperationClass => OperationClass.AdminConfigChange;

        public Task<OperationProposalPlan?> PlanAsync(
            OperationGatewayRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<OperationProposalPlan?>(new OperationProposalPlan { Summary = "stub plan" });

        public Task<string?> ExecuteAsync(
            OperationGatewayRequest request,
            string? executionPayload,
            CancellationToken cancellationToken = default)
        {
            Executed = true;
            ExecutionCount++;
            return Task.FromResult<string?>("exec-1");
        }
    }

    internal sealed class RecordingProposalNotifier : IProposalNotifier
    {
        public int PendingCount { get; private set; }

        public int ResolvedCount { get; private set; }

        public void Reset()
        {
            PendingCount = 0;
            ResolvedCount = 0;
        }

        public Task NotifyPendingAsync(OperationProposal proposal, CancellationToken cancellationToken = default)
        {
            PendingCount++;
            return Task.CompletedTask;
        }

        public Task NotifyResolvedAsync(OperationProposal proposal, CancellationToken cancellationToken = default)
        {
            ResolvedCount++;
            return Task.CompletedTask;
        }
    }
}
