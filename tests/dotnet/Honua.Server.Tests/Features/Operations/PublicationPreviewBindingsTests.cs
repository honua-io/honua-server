// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.OperationsPublishing;

public sealed class PublicationPreviewBindingsTests
{
    private static readonly LayerPublicationBase Base = new(7, "\"etag-7\"");
    private readonly VolatileOperationInstanceStore _store = new();
    private readonly PreviewClock _clock = new();

    [UnitTest]
    public async Task Reference_SurvivesExecutorRecreation_AndAcceptsIdenticalPublishRequest()
    {
        var request = Request();
        var created = await Bindings().CreateAsync(request, Context(), Base, CancellationToken.None);
        var replay = WithReference(request with { OperationId = "service.publish" }, created.Reference);

        await Bindings().VerifyAsync(replay, Context(), Base, CancellationToken.None);
        created.ExpiresAt.Should().Be(_clock.GetUtcNow() + PublicationPreviewBindings.Lifetime);
        var retained = await _store.GetAsync(created.Reference);
        retained!.Result!.Details.Values.Should().NotContain(value => value.Contains("Host=", StringComparison.Ordinal));
    }

    [UnitTest]
    public async Task Reference_RejectsAnotherActorOrTenant()
    {
        var created = await Bindings().CreateAsync(Request(), Context(), Base, CancellationToken.None);
        var request = WithReference(Request(), created.Reference);

        var otherActor = () => Bindings().VerifyAsync(request, Context() with { PrincipalId = "other" }, Base, CancellationToken.None);
        var otherTenant = () => Bindings().VerifyAsync(request, Context() with { TenantId = "other" }, Base, CancellationToken.None);

        await otherActor.Should().ThrowAsync<InvalidOperationException>();
        await otherTenant.Should().ThrowAsync<InvalidOperationException>();
    }

    [UnitTest]
    public async Task Reference_RejectsChangedSourceOptionsFieldsAndMetadataBase()
    {
        var created = await Bindings().CreateAsync(Request(), Context(), Base, CancellationToken.None);
        var request = WithReference(Request(), created.Reference);
        var changed = new Dictionary<string, string?>(request.Parameters) { ["table"] = "other" };
        var changedOptions = new Dictionary<string, string?>(request.Parameters) { ["enabled"] = "false" };
        foreach (var replay in new[]
        {
            request with { ConnectionId = "22222222-2222-2222-2222-222222222222" },
            request with { ServiceName = "other" },
            request with { Fields = ["id"] },
            request with { Parameters = changed },
            request with { Parameters = changedOptions }
        })
        {
            var act = () => Bindings().VerifyAsync(replay, Context(), Base, CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        var revision = () => Bindings().VerifyAsync(request, Context(), new LayerPublicationBase(8, Base.Etag), CancellationToken.None);
        var etag = () => Bindings().VerifyAsync(request, Context(), new LayerPublicationBase(7, "changed"), CancellationToken.None);
        await revision.Should().ThrowAsync<InvalidOperationException>();
        await etag.Should().ThrowAsync<InvalidOperationException>();
    }

    [UnitTest]
    public async Task ReadingReference_DoesNotRenewOriginalExpiry()
    {
        var created = await Bindings().CreateAsync(Request(), Context(), Base, CancellationToken.None);
        var request = WithReference(Request(), created.Reference);
        _clock.Now = created.ExpiresAt - TimeSpan.FromTicks(1);
        await Bindings().VerifyAsync(request, Context(), Base, CancellationToken.None);
        _clock.Now = created.ExpiresAt;
        var expired = () => Bindings().VerifyAsync(request, Context(), Base, CancellationToken.None);
        await expired.Should().ThrowAsync<InvalidOperationException>();
    }

    [UnitTest]
    public async Task Reference_RejectsMalformedAndUnknownValues()
    {
        foreach (var reference in new[] { "", "publication-preview-bogus", "publication-preview-" + new string('a', 64) })
        {
            var act = () => Bindings().VerifyAsync(WithReference(Request(), reference), Context(), Base, CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
    }

    [UnitTest]
    public async Task DedicatedPublishNullOptions_MatchOmittedPreviewOptions_ButExplicitRootDoesNot()
    {
        var created = await Bindings().CreateAsync(Request(), Context(), Base, CancellationToken.None);
        var request = WithReference(Request(), created.Reference);
        var parameters = new Dictionary<string, string?>(request.Parameters)
        {
            ["geometryColumn"] = null,
            ["description"] = null,
            [ServicePublishMetadataPin.RevisionKey] = "7",
            [ServicePublishMetadataPin.EtagKey] = Base.Etag
        };
        await Bindings().VerifyAsync(request with { Parameters = parameters }, Context(), Base, CancellationToken.None);
        parameters["folderId"] = null;
        var root = () => Bindings().VerifyAsync(request with { Parameters = parameters }, Context(), Base, CancellationToken.None);
        await root.Should().ThrowAsync<InvalidOperationException>();
    }

    private PublicationPreviewBindings Bindings() => new(_store, _clock);

    private static OperationRequest Request() => new()
    {
        OperationId = "service.publication.preview",
        ConnectionId = "11111111-1111-1111-1111-111111111111",
        ServiceName = "parcels",
        Fields = ["id", "name"],
        Parameters = new Dictionary<string, string?>
        {
            ["schema"] = "public", ["table"] = "parcels", ["layerName"] = "Parcels"
        }
    };

    private static OperationRequest WithReference(OperationRequest request, string reference) => request with
    {
        Parameters = new Dictionary<string, string?>(request.Parameters) { [PublicationPreviewBindings.ReferenceKey] = reference }
    };

    private static OperationPolicyContext Context() => new() { PrincipalId = "actor", TenantId = "tenant" };

    private sealed class PreviewClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
