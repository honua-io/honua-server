// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
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
    // These suites exercise composition and persistence. Agent approval has separate suites.
    internal static WebAppFixture CreateCompositionFixture() => new WebAppFixture()
        .ConfigureWebHost(builder => builder.UseSetting("Licensing:DevGrantEdition", "Pro"))
        .ConfigureServices(services =>
        {
            services.RemoveAll<IStudioPackageStore>();
            services.AddSingleton<IStudioPackageStore, InMemoryStudioPackageStore>();
            services.RemoveAll<IOperationPolicyDecisionPoint>();
            services.AddSingleton<IOperationPolicyDecisionPoint, CompositionTestPolicy>();
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

    // Admit only updates of this fixture's own drafts, while retaining the canonical runtime.
    private sealed class CompositionTestPolicy : IOperationPolicyDecisionPoint
    {
        public Task<PolicyDecision> EvaluateAsync(
            IOperationDescriptor descriptor,
            OperationRequest request,
            OperationPolicyContext context,
            CancellationToken cancellationToken = default)
        {
            var ownedUpdate = descriptor.OperationId == StudioDraftMutationContext.UpdateOperationId
                && request.OperationId == descriptor.OperationId
                && context.PrincipalId == WebAppFixture.DevelopmentBypassActorId
                && request.VerifiedStudioDraftOwnerId == context.PrincipalId;
            return Task.FromResult(ownedUpdate ? PolicyDecision.Allowed : new PolicyDecision
            {
                Kind = PolicyDecisionKind.Deny,
                Reason = "Composition test policy permits only fixture-owned draft updates.",
            });
        }
    }
}
