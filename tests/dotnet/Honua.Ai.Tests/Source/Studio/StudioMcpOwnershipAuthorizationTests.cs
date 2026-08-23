// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Studio;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Studio;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Ownership-parity matrix for the Studio MCP lifecycle surface (#3412).
/// Exercises the real <see cref="StudioAuthorizationService"/> behind a
/// recording adapter so the tests prove both policy behavior and the exact
/// loaded owner/operation/resource id each MCP family supplies to it.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class StudioMcpOwnershipAuthorizationTests
{
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string Delegate = "delegate";

    private static readonly Guid DraftId = Guid.Parse("51515151-5151-5151-5151-515151515151");
    private static readonly Guid ItemId = Guid.Parse("61616161-6161-6161-6161-616161616161");

    [Theory]
    [InlineData(DraftToolFamily.Read, CallerKind.Anonymous)]
    [InlineData(DraftToolFamily.Read, CallerKind.Owner)]
    [InlineData(DraftToolFamily.Read, CallerKind.NonOwner)]
    [InlineData(DraftToolFamily.Read, CallerKind.DelegatedOperator)]
    [InlineData(DraftToolFamily.Read, CallerKind.Admin)]
    [InlineData(DraftToolFamily.Update, CallerKind.Anonymous)]
    [InlineData(DraftToolFamily.Update, CallerKind.Owner)]
    [InlineData(DraftToolFamily.Update, CallerKind.NonOwner)]
    [InlineData(DraftToolFamily.Update, CallerKind.DelegatedOperator)]
    [InlineData(DraftToolFamily.Update, CallerKind.Admin)]
    [InlineData(DraftToolFamily.Validate, CallerKind.Anonymous)]
    [InlineData(DraftToolFamily.Validate, CallerKind.Owner)]
    [InlineData(DraftToolFamily.Validate, CallerKind.NonOwner)]
    [InlineData(DraftToolFamily.Validate, CallerKind.DelegatedOperator)]
    [InlineData(DraftToolFamily.Validate, CallerKind.Admin)]
    [InlineData(DraftToolFamily.Preview, CallerKind.Anonymous)]
    [InlineData(DraftToolFamily.Preview, CallerKind.Owner)]
    [InlineData(DraftToolFamily.Preview, CallerKind.NonOwner)]
    [InlineData(DraftToolFamily.Preview, CallerKind.DelegatedOperator)]
    [InlineData(DraftToolFamily.Preview, CallerKind.Admin)]
    [InlineData(DraftToolFamily.Composition, CallerKind.Anonymous)]
    [InlineData(DraftToolFamily.Composition, CallerKind.Owner)]
    [InlineData(DraftToolFamily.Composition, CallerKind.NonOwner)]
    [InlineData(DraftToolFamily.Composition, CallerKind.DelegatedOperator)]
    [InlineData(DraftToolFamily.Composition, CallerKind.Admin)]
    [InlineData(DraftToolFamily.PublicationProposal, CallerKind.Anonymous)]
    [InlineData(DraftToolFamily.PublicationProposal, CallerKind.Owner)]
    [InlineData(DraftToolFamily.PublicationProposal, CallerKind.NonOwner)]
    [InlineData(DraftToolFamily.PublicationProposal, CallerKind.DelegatedOperator)]
    [InlineData(DraftToolFamily.PublicationProposal, CallerKind.Admin)]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_*")]
    public async Task ExistingDraftFamilies_ApplyCanonicalLoadedOwnerPolicy(
        DraftToolFamily family,
        CallerKind callerKind)
    {
        var draft = BuildDraft(Alice);
        var lifecycle = Substitute.For<IStudioPackageLifecycleService>();
        lifecycle.GetDraftAsync(DraftId, Arg.Any<CancellationToken>()).Returns(draft);
        lifecycle.UpdateDraftAsync(
                DraftId,
                Arg.Any<UpdateStudioPackageDraftCommand>(),
                Arg.Any<CancellationToken>())
            .Returns(call => draft with
            {
                OwnerId = call.Arg<UpdateStudioPackageDraftCommand>().OwnerId ?? draft.OwnerId,
                Generation = 2,
            });

        var validator = Substitute.For<IStudioPackageValidator>();
        validator.Validate(Arg.Any<StudioPackageEnvelope>())
            .Returns(new StudioValidationSummary { Status = StudioPackageValidationStatus.Valid });
        var jobService = Substitute.For<IGeoprocessingJobService>();
        jobService.EnsureCallerAuthorizedAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<OperatorResourceType>(),
                Arg.Any<OperatorOperation>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var evaluator = Substitute.For<IOperatorAuthorizationEvaluator>();
        evaluator.EvaluateAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<OperatorAuthorizationRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(AccessDecision.Allowed("delegate grant"));
        var authorization = BuildAuthorization(evaluator);
        var context = BuildContext(callerKind, lifecycle, validator, authorization);
        var (tool, arguments) = BuildInvocation(family, jobService);

        var act = () => tool.InvokeAsync(context, arguments, CancellationToken.None);
        var allowed = callerKind is CallerKind.Owner or CallerKind.Admin;

        if (allowed)
        {
            var result = await act();
            result.IsError.Should().BeFalse();

            authorization.Calls.Should().ContainSingle();
            var call = authorization.Calls.Single();
            call.Operation.Should().Be(ExpectedStudioOperation(family));
            call.ResourceOwnerId.Should().Be(Alice);
            call.ResourceId.Should().Be(DraftId.ToString("D"));
        }
        else
        {
            var failure = await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
            await lifecycle.DidNotReceive().UpdateDraftAsync(
                Arg.Any<Guid>(),
                Arg.Any<UpdateStudioPackageDraftCommand>(),
                Arg.Any<CancellationToken>());

            if (callerKind == CallerKind.Anonymous)
            {
                failure.Which.RequiresAuthentication.Should().BeTrue();
                authorization.Calls.Should().BeEmpty("anonymous callers are rejected before any resource lookup");
                await lifecycle.DidNotReceive().GetDraftAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
            }
            else
            {
                failure.Which.PolicyCode.Should().Be(StudioAuthorizationService.CrossUserDeniedCode);
                authorization.Calls.Should().ContainSingle();
                authorization.Calls.Single().Operation.Should().Be(ExpectedStudioOperation(family));

                var error = McpToolHelpers.ErrorResult(failure.Which).StructuredContent!.Value;
                error.GetProperty("code").GetString().Should().Be(McpErrorMapper.Codes.PermissionDenied);
                error.GetProperty("studioAuthorizationCode").GetString()
                    .Should().Be(StudioAuthorizationService.CrossUserDeniedCode,
                        "MCP retains the same governed denial code REST exposes");
            }
        }

        var expectedOperatorOperation = family is DraftToolFamily.Read or DraftToolFamily.Validate or DraftToolFamily.Preview
            ? OperatorOperation.Read
            : OperatorOperation.Create;
        if (callerKind == CallerKind.Anonymous)
        {
            await jobService.DidNotReceive().EnsureCallerAuthorizedAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<OperatorResourceType>(),
                Arg.Any<OperatorOperation>(),
                Arg.Any<CancellationToken>());
        }
        else
        {
            await jobService.Received(1).EnsureCallerAuthorizedAsync(
                Arg.Any<ClaimsPrincipal>(),
                OperatorResourceType.StudioDraft,
                expectedOperatorOperation,
                Arg.Any<CancellationToken>());
        }

        if (callerKind == CallerKind.DelegatedOperator)
        {
            await evaluator.DidNotReceive().EvaluateAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<OperatorAuthorizationRequest>(),
                Arg.Any<CancellationToken>());
        }
    }

    [Theory]
    [InlineData(CallerKind.Owner, true)]
    [InlineData(CallerKind.NonOwner, false)]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_get_draft")]
    public async Task GetDraft_LegacyMcpOwnerKey_OnlyOriginalPrincipalRetainsAccess(
        CallerKind callerKind,
        bool expectedAllowed)
    {
        const string legacyAliceOwnerId = "Test:sub:alice";
        var lifecycle = Substitute.For<IStudioPackageLifecycleService>();
        lifecycle.GetDraftAsync(DraftId, Arg.Any<CancellationToken>())
            .Returns(BuildDraft(legacyAliceOwnerId));
        var authorization = BuildAuthorization();
        var context = BuildContext(callerKind, lifecycle, validator: null, authorization);
        var tool = new GetStudioDraftTool(
            Substitute.For<IGeoprocessingJobService>(),
            NullLogger<GetStudioDraftTool>.Instance);
        var arguments = McpTestFactory.ParseJson($$"""{"draftId":"{{DraftId:D}}"}""");

        var act = () => tool.InvokeAsync(context, arguments, CancellationToken.None);

        if (expectedAllowed)
        {
            var result = await act();
            result.IsError.Should().BeFalse();
            authorization.Calls.Should().ContainSingle(call =>
                call.Operation == StudioAuthorizationOperation.ReadDraft &&
                call.ResourceOwnerId == Alice);
        }
        else
        {
            var failure = await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
            failure.Which.PolicyCode.Should().Be(StudioAuthorizationService.CrossUserDeniedCode);
            authorization.Calls.Should().ContainSingle(call =>
                call.Operation == StudioAuthorizationOperation.ReadDraft &&
                call.ResourceOwnerId == legacyAliceOwnerId);
        }
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_get_draft")]
    public async Task GetDraft_AmbiguousLegacyAuthenticatedOwner_FailsClosed()
    {
        var lifecycle = Substitute.For<IStudioPackageLifecycleService>();
        lifecycle.GetDraftAsync(DraftId, Arg.Any<CancellationToken>())
            .Returns(BuildDraft("Test:authenticated"));
        var authorization = BuildAuthorization();
        var context = BuildContext(CallerKind.Owner, lifecycle, validator: null, authorization);
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", Alice),
            new Claim(ClaimTypes.Role, "creator"),
        ], "Test"));
        var tool = new GetStudioDraftTool(
            Substitute.For<IGeoprocessingJobService>(),
            NullLogger<GetStudioDraftTool>.Instance);
        var arguments = McpTestFactory.ParseJson($$"""{"draftId":"{{DraftId:D}}"}""");

        var act = () => tool.InvokeAsync(context, arguments, CancellationToken.None);

        var failure = await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        failure.Which.PolicyCode.Should().Be(StudioAuthorizationService.CrossUserDeniedCode);
        authorization.Calls.Should().ContainSingle(call =>
            call.ResourceOwnerId == "Test:authenticated");
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_get_draft")]
    public async Task GetDraft_LegacyApiKeyNameOwner_FailsClosed()
    {
        const string legacySharedNameOwnerId = "ApiKey:name:shared-studio-key";
        var lifecycle = Substitute.For<IStudioPackageLifecycleService>();
        lifecycle.GetDraftAsync(DraftId, Arg.Any<CancellationToken>())
            .Returns(BuildDraft(legacySharedNameOwnerId));
        var authorization = BuildAuthorization();
        var context = BuildContext(CallerKind.Owner, lifecycle, validator: null, authorization);
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "shared-studio-key"),
            new Claim("api_key_id", "11111111-2222-3333-4444-555555555555"),
            new Claim(ClaimTypes.Role, "creator"),
        ], "ApiKey"));
        var tool = new GetStudioDraftTool(
            Substitute.For<IGeoprocessingJobService>(),
            NullLogger<GetStudioDraftTool>.Instance);
        var arguments = McpTestFactory.ParseJson($$"""{"draftId":"{{DraftId:D}}"}""");

        var act = () => tool.InvokeAsync(context, arguments, CancellationToken.None);

        var failure = await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        failure.Which.PolicyCode.Should().Be(StudioAuthorizationService.CrossUserDeniedCode);
        authorization.Calls.Should().ContainSingle(call =>
            call.ResourceOwnerId == legacySharedNameOwnerId);
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_create_draft")]
    public async Task CreateDraft_NonAdminDerivesOwnerFromAuthenticatedPrincipal()
    {
        CreateStudioPackageDraftCommand? captured = null;
        var lifecycle = Substitute.For<IStudioPackageLifecycleService>();
        lifecycle.CreateDraftAsync(Arg.Any<CreateStudioPackageDraftCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.Arg<CreateStudioPackageDraftCommand>();
                return BuildDraft(captured.OwnerId);
            });
        var authorization = BuildAuthorization();
        var context = BuildContext(CallerKind.Owner, lifecycle, validator: null, authorization);
        var tool = new CreateStudioDraftTool(
            Substitute.For<IGeoprocessingJobService>(),
            NullLogger<CreateStudioDraftTool>.Instance);

        var result = await tool.InvokeAsync(
            context,
            McpTestFactory.ParseJson(
                """{"packageKey":"owner-map","family":"map","schemaVersion":"1.0","ownerId":"bob"}"""),
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        captured.Should().NotBeNull();
        captured!.OwnerId.Should().Be(Alice);
        captured.ActorId.Should().Be(Alice);
        authorization.Calls.Should().ContainSingle(call =>
            call.Operation == StudioAuthorizationOperation.CreateDraft &&
            call.ResourceOwnerId == Alice &&
            call.ResourceId == null);
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_create_draft")]
    public async Task CreateDraft_NonOwnerCannotTargetExistingItemEvenWithGenericGrant()
    {
        var lifecycle = Substitute.For<IStudioPackageLifecycleService>();
        lifecycle.GetPointersAsync(ItemId, Arg.Any<CancellationToken>()).Returns(new StudioContentItemPointers
        {
            ItemId = ItemId,
            OwnerId = Alice,
        });
        var authorization = BuildAuthorization();
        var context = BuildContext(CallerKind.NonOwner, lifecycle, validator: null, authorization);
        var jobService = Substitute.For<IGeoprocessingJobService>();
        var tool = new CreateStudioDraftTool(jobService, NullLogger<CreateStudioDraftTool>.Instance);

        var act = () => tool.InvokeAsync(
            context,
            McpTestFactory.ParseJson(
                $$"""{"itemId":"{{ItemId:D}}","packageKey":"guessed-item","family":"map","schemaVersion":"1.0"}"""),
            CancellationToken.None);

        var failure = await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        failure.Which.PolicyCode.Should().Be(StudioAuthorizationService.CrossUserDeniedCode);
        authorization.Calls.Should().ContainSingle(call =>
            call.Operation == StudioAuthorizationOperation.CreateDraft &&
            call.ResourceOwnerId == Alice &&
            call.ResourceId == ItemId.ToString("D"));
        await lifecycle.DidNotReceive().CreateDraftAsync(
            Arg.Any<CreateStudioPackageDraftCommand>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_create_draft")]
    public async Task CreateDraft_AdminMayAssignExplicitOwner()
    {
        CreateStudioPackageDraftCommand? captured = null;
        var lifecycle = Substitute.For<IStudioPackageLifecycleService>();
        lifecycle.CreateDraftAsync(Arg.Any<CreateStudioPackageDraftCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.Arg<CreateStudioPackageDraftCommand>();
                return BuildDraft(captured.OwnerId);
            });
        var authorization = BuildAuthorization();
        var context = BuildContext(CallerKind.Admin, lifecycle, validator: null, authorization);
        var tool = new CreateStudioDraftTool(
            Substitute.For<IGeoprocessingJobService>(),
            NullLogger<CreateStudioDraftTool>.Instance);

        await tool.InvokeAsync(
            context,
            McpTestFactory.ParseJson(
                """{"packageKey":"admin-map","family":"map","schemaVersion":"1.0","ownerId":"bob"}"""),
            CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.OwnerId.Should().Be(Bob);
        captured.ActorId.Should().Be("admin-1");
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_create_draft")]
    public async Task CreateDraft_AnonymousCallerNeverPersists()
    {
        var lifecycle = Substitute.For<IStudioPackageLifecycleService>();
        var authorization = BuildAuthorization();
        var context = BuildContext(CallerKind.Anonymous, lifecycle, validator: null, authorization);
        var tool = new CreateStudioDraftTool(
            Substitute.For<IGeoprocessingJobService>(),
            NullLogger<CreateStudioDraftTool>.Instance);

        var act = () => tool.InvokeAsync(
            context,
            McpTestFactory.ParseJson("""{"packageKey":"anonymous-map","family":"map","schemaVersion":"1.0"}"""),
            CancellationToken.None);

        var failure = await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        failure.Which.RequiresAuthentication.Should().BeTrue();
        await lifecycle.DidNotReceive().CreateDraftAsync(
            Arg.Any<CreateStudioPackageDraftCommand>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_update_draft")]
    public async Task UpdateDraft_NonAdminCannotTransferOwnership()
    {
        UpdateStudioPackageDraftCommand? captured = null;
        var draft = BuildDraft(Alice);
        var lifecycle = Substitute.For<IStudioPackageLifecycleService>();
        lifecycle.GetDraftAsync(DraftId, Arg.Any<CancellationToken>()).Returns(draft);
        lifecycle.UpdateDraftAsync(DraftId, Arg.Any<UpdateStudioPackageDraftCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.Arg<UpdateStudioPackageDraftCommand>();
                return draft with { Generation = 2, OwnerId = captured.OwnerId ?? draft.OwnerId };
            });
        var authorization = BuildAuthorization();
        var context = BuildContext(CallerKind.Owner, lifecycle, validator: null, authorization);
        var tool = new UpdateStudioDraftTool(
            Substitute.For<IGeoprocessingJobService>(),
            NullLogger<UpdateStudioDraftTool>.Instance);

        await tool.InvokeAsync(
            context,
            McpTestFactory.ParseJson(
                $$"""{"draftId":"{{DraftId:D}}","generation":1,"packageKey":"owner-map","schemaVersion":"1.0","ownerId":"bob"}"""),
            CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.OwnerId.Should().Be(Alice);
        captured.ActorId.Should().Be(Alice);
    }

    private static (IMcpTool Tool, System.Text.Json.JsonElement Arguments) BuildInvocation(
        DraftToolFamily family,
        IGeoprocessingJobService jobService) => family switch
        {
            DraftToolFamily.Read =>
                (new GetStudioDraftTool(jobService, NullLogger<GetStudioDraftTool>.Instance),
                    McpTestFactory.ParseJson($$"""{"draftId":"{{DraftId:D}}"}""")),
            DraftToolFamily.Update =>
                (new UpdateStudioDraftTool(jobService, NullLogger<UpdateStudioDraftTool>.Instance),
                    McpTestFactory.ParseJson(
                        $$"""{"draftId":"{{DraftId:D}}","generation":1,"packageKey":"owner-map","schemaVersion":"1.0","ownerId":"bob"}""")),
            DraftToolFamily.Validate =>
                (new ValidateStudioDraftTool(jobService, NullLogger<ValidateStudioDraftTool>.Instance),
                    McpTestFactory.ParseJson($$"""{"draftId":"{{DraftId:D}}"}""")),
            DraftToolFamily.Preview =>
                (new PreviewStudioDraftTool(jobService, NullLogger<PreviewStudioDraftTool>.Instance),
                    McpTestFactory.ParseJson($$"""{"draftId":"{{DraftId:D}}"}""")),
            DraftToolFamily.Composition =>
                (new AddStudioLayerTool(jobService, NullLogger<AddStudioLayerTool>.Instance),
                    McpTestFactory.ParseJson(
                        $$$"""{"draftId":"{{{DraftId:D}}}","generation":1,"layer":{"id":"parcels"}}""")),
            DraftToolFamily.PublicationProposal =>
                (new ProposeStudioPublicationTool(jobService, NullLogger<ProposeStudioPublicationTool>.Instance),
                    McpTestFactory.ParseJson(
                        $$"""{"draftId":"{{DraftId:D}}","generation":1,"route":"/studio/owner-map"}""")),
            _ => throw new ArgumentOutOfRangeException(nameof(family), family, null),
        };

    private static StudioAuthorizationOperation ExpectedStudioOperation(DraftToolFamily family) => family switch
    {
        DraftToolFamily.Read => StudioAuthorizationOperation.ReadDraft,
        DraftToolFamily.Validate or DraftToolFamily.Preview => StudioAuthorizationOperation.ValidateDraft,
        _ => StudioAuthorizationOperation.UpdateDraft,
    };

    private static DefaultHttpContext BuildContext(
        CallerKind callerKind,
        IStudioPackageLifecycleService lifecycle,
        IStudioPackageValidator? validator,
        IStudioAuthorizationService authorization)
    {
        var context = McpTestFactory.AuthenticatedHttpContextWithServices(services =>
        {
            services.AddSingleton(lifecycle);
            services.AddSingleton(authorization);
            if (validator is not null)
            {
                services.AddSingleton(validator);
            }
        });
        context.User = Principal(callerKind);
        return context;
    }

    private static ClaimsPrincipal Principal(CallerKind callerKind) => callerKind switch
    {
        CallerKind.Anonymous => new ClaimsPrincipal(new ClaimsIdentity()),
        CallerKind.Owner => UserPrincipal(Alice),
        CallerKind.NonOwner => UserPrincipal(Bob),
        CallerKind.DelegatedOperator => UserPrincipal(Delegate, "operator"),
        CallerKind.Admin => UserPrincipal("admin-1", "admin"),
        _ => throw new ArgumentOutOfRangeException(nameof(callerKind), callerKind, null),
    };

    private static ClaimsPrincipal UserPrincipal(string id, string role = "creator") =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, id),
            new Claim(ClaimTypes.Name, id),
            new Claim(ClaimTypes.Role, role),
        ], "Test"));

    private static RecordingStudioAuthorizationService BuildAuthorization(
        IOperatorAuthorizationEvaluator? evaluator = null)
    {
        evaluator ??= Substitute.For<IOperatorAuthorizationEvaluator>();
        var inner = new StudioAuthorizationService(
            evaluator,
            new StaticOptionsMonitor<StudioEndUserAuthorizationOptions>(
                new StudioEndUserAuthorizationOptions { Enabled = true }),
            new StaticOptionsMonitor<AdminRoleOptions>(new AdminRoleOptions()));
        return new RecordingStudioAuthorizationService(inner);
    }

    private static StudioPackageDraft BuildDraft(string? ownerId)
    {
        using var body = System.Text.Json.JsonDocument.Parse(
            """{"format":"honua_map_package.v1","layers":[],"widgets":[]}""");
        return new StudioPackageDraft
        {
            DraftId = DraftId,
            ItemId = ItemId,
            PackageKey = "owner-map",
            Family = StudioPackageFamily.Map,
            OwnerId = ownerId,
            Envelope = new StudioPackageEnvelope
            {
                Family = StudioPackageFamily.Map,
                SchemaVersion = "1.0",
                Body = body.RootElement.Clone(),
            },
            Generation = 1,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };
    }

    public enum DraftToolFamily
    {
        Read,
        Update,
        Validate,
        Preview,
        Composition,
        PublicationProposal,
    }

    public enum CallerKind
    {
        Anonymous,
        Owner,
        NonOwner,
        DelegatedOperator,
        Admin,
    }

    private sealed record AuthorizationCall(
        StudioAuthorizationOperation Operation,
        string? ResourceOwnerId,
        string? ResourceId);

    private sealed class RecordingStudioAuthorizationService(IStudioAuthorizationService inner)
        : IStudioAuthorizationService
    {
        public List<AuthorizationCall> Calls { get; } = [];

        public bool IsEndUserAuthorizationEnabled => inner.IsEndUserAuthorizationEnabled;

        public bool IsAdmin(ClaimsPrincipal principal) => inner.IsAdmin(principal);

        public string? ResolveCallerId(ClaimsPrincipal principal) => inner.ResolveCallerId(principal);

        public async Task<StudioAuthorizationDecision> AuthorizeAsync(
            ClaimsPrincipal principal,
            string? callerId,
            StudioAuthorizationOperation operation,
            string? resourceOwnerId,
            bool isPubliclyReadable = false,
            string? resourceId = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new AuthorizationCall(operation, resourceOwnerId, resourceId));
            return await inner.AuthorizeAsync(
                principal,
                callerId,
                operation,
                resourceOwnerId,
                isPubliclyReadable,
                resourceId,
                cancellationToken);
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<T, string?> listener) => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
