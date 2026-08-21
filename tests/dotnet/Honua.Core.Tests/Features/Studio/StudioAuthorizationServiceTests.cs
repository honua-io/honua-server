// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Studio;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;

namespace Honua.Core.Tests.Features.Studio;

/// <summary>
/// Role-fixture matrix for <see cref="StudioAuthorizationService"/> (honua-server#3001): flag
/// on/off, admin vs. non-admin, own vs. cross-user resources, and the elevated
/// (publish-request/rollback) operator-grant tier.
/// </summary>
public sealed class StudioAuthorizationServiceTests
{
    private const string Alice = "alice";
    private const string Bob = "bob";

    [UnitTest]
    public async Task AuthorizeAsync_FlagOff_AdminAllowed()
    {
        var service = BuildService(enabled: false, out _);
        var decision = await service.AuthorizeAsync(
            AdminPrincipal(), Alice, StudioAuthorizationOperation.ReadDraft, resourceOwnerId: Bob);

        Assert.True(decision.IsAllowed);
    }

    [UnitTest]
    public async Task AuthorizeAsync_FlagOff_NonAdminDeniedEvenOnOwnResource()
    {
        var service = BuildService(enabled: false, out _);
        var decision = await service.AuthorizeAsync(
            UserPrincipal(Alice), Alice, StudioAuthorizationOperation.ReadDraft, resourceOwnerId: Alice);

        Assert.False(decision.IsAllowed);
        Assert.Equal(StudioAuthorizationService.EndUserModeDisabledCode, decision.Code);
    }

    [UnitTest]
    public async Task AuthorizeAsync_FlagOn_AdminAllowedRegardlessOfOwnership()
    {
        var service = BuildService(enabled: true, out _);
        var decision = await service.AuthorizeAsync(
            AdminPrincipal(), "admin-1", StudioAuthorizationOperation.DeleteDraft, resourceOwnerId: Bob);

        Assert.True(decision.IsAllowed);
        Assert.False(decision.IsElevated);
    }

    [UnitTest]
    public async Task AuthorizeAsync_FlagOn_NonAdminOwnResource_BaselineOperationAllowedWithoutGrant()
    {
        var service = BuildService(enabled: true, out var evaluator);
        var decision = await service.AuthorizeAsync(
            UserPrincipal(Alice), Alice, StudioAuthorizationOperation.UpdateDraft, resourceOwnerId: Alice);

        Assert.True(decision.IsAllowed);
        Assert.False(decision.IsElevated);
        Assert.Empty(evaluator.Requests);
    }

    [UnitTest]
    public async Task AuthorizeAsync_FlagOn_NonAdminOwnerlessExistingResource_DeniedFailClosed()
    {
        // honua-server#3001 follow-up (P2): owner_id is a nullable column -- legacy rows
        // created before the ownership migration (or a partial backfill) may still have no
        // recorded owner. A null owner must never be treated as "owned by whoever asks" (that
        // would let any authenticated caller claim it); it fails closed to admin-only until an
        // owner is assigned. Endpoints never call AuthorizeAsync with resourceOwnerId: null for
        // a brand-new resource -- StudioPackageEndpoints resolves ownership to the creating
        // caller before persisting and only authorizes against an existing resource's recorded
        // owner thereafter, so this null-owner path is exclusively the "no owner assigned yet"
        // case in practice.
        var service = BuildService(enabled: true, out _);
        var decision = await service.AuthorizeAsync(
            UserPrincipal(Alice), Alice, StudioAuthorizationOperation.ReadDraft, resourceOwnerId: null);

        Assert.False(decision.IsAllowed);
        Assert.Equal(StudioAuthorizationService.CrossUserDeniedCode, decision.Code);
    }

    [UnitTest]
    public async Task AuthorizeAsync_FlagOn_NonAdminOwnerlessResource_PubliclyReadableStillAllowed()
    {
        // A null owner does not defeat public-read visibility: an ownerless-but-published item
        // must remain readable by any authenticated caller, exactly like an owned published item.
        var service = BuildService(enabled: true, out _);
        var decision = await service.AuthorizeAsync(
            UserPrincipal(Alice),
            Alice,
            StudioAuthorizationOperation.ReadContentItem,
            resourceOwnerId: null,
            isPubliclyReadable: true);

        Assert.True(decision.IsAllowed);
    }

    [UnitTest]
    public async Task AuthorizeAsync_FlagOn_AdminOwnerlessResource_AllowedRegardlessOfOwnership()
    {
        var service = BuildService(enabled: true, out _);
        var decision = await service.AuthorizeAsync(
            AdminPrincipal(), "admin-1", StudioAuthorizationOperation.DeleteDraft, resourceOwnerId: null);

        Assert.True(decision.IsAllowed);
    }

    [UnitTest]
    public async Task AuthorizeAsync_FlagOn_NonAdminCrossUser_BaselineOperationDenied()
    {
        var service = BuildService(enabled: true, out _);
        var decision = await service.AuthorizeAsync(
            UserPrincipal(Alice), Alice, StudioAuthorizationOperation.ReadDraft, resourceOwnerId: Bob);

        Assert.False(decision.IsAllowed);
        Assert.Equal(StudioAuthorizationService.CrossUserDeniedCode, decision.Code);
    }

    [UnitTest]
    public async Task AuthorizeAsync_FlagOn_NonAdminCrossUser_PublishedReadAllowed()
    {
        var service = BuildService(enabled: true, out _);
        var decision = await service.AuthorizeAsync(
            UserPrincipal(Alice),
            Alice,
            StudioAuthorizationOperation.ReadContentItem,
            resourceOwnerId: Bob,
            isPubliclyReadable: true);

        Assert.True(decision.IsAllowed);
    }

    [UnitTest]
    public async Task AuthorizeAsync_FlagOn_NonAdminCrossUser_NonReadOperationIgnoresPubliclyReadable()
    {
        var service = BuildService(enabled: true, out _);
        var decision = await service.AuthorizeAsync(
            UserPrincipal(Alice),
            Alice,
            StudioAuthorizationOperation.UpdateDraft,
            resourceOwnerId: Bob,
            isPubliclyReadable: true);

        Assert.False(decision.IsAllowed);
        Assert.Equal(StudioAuthorizationService.CrossUserDeniedCode, decision.Code);
    }

    [UnitTest]
    public async Task AuthorizeAsync_FlagOn_Unauthenticated_Denied()
    {
        var service = BuildService(enabled: true, out _);
        var decision = await service.AuthorizeAsync(
            new ClaimsPrincipal(new ClaimsIdentity()), callerId: null,
            StudioAuthorizationOperation.ReadDraft, resourceOwnerId: null);

        Assert.False(decision.IsAllowed);
        Assert.Equal(StudioAuthorizationService.AuthenticationRequiredCode, decision.Code);
    }

    [UnitTest]
    public async Task AuthorizeAsync_FlagOn_NonAdminOwnResource_ElevatedOperationDeniedWithoutGrant()
    {
        var service = BuildService(enabled: true, out _);
        var decision = await service.AuthorizeAsync(
            UserPrincipal(Alice), Alice, StudioAuthorizationOperation.PublishRequest, resourceOwnerId: Alice);

        Assert.False(decision.IsAllowed);
        Assert.True(decision.IsElevated);
        Assert.Equal(StudioAuthorizationService.ElevatedGrantRequiredCode, decision.Code);
    }

    [UnitTest]
    public async Task AuthorizeAsync_FlagOn_NonAdminOwnResource_ElevatedOperationAllowedWithOwnGrant()
    {
        var service = BuildService(enabled: true, out var evaluator);
        evaluator.Allow(OperatorResourceType.StudioDraft, "own", OperatorOperation.Publish);

        var decision = await service.AuthorizeAsync(
            UserPrincipal(Alice), Alice, StudioAuthorizationOperation.PublishRequest, resourceOwnerId: Alice);

        Assert.True(decision.IsAllowed);
        Assert.True(decision.IsElevated);
    }

    [UnitTest]
    public async Task AuthorizeAsync_FlagOn_NonAdminOwnResource_RollbackUsesRollbackOperatorOperation()
    {
        var service = BuildService(enabled: true, out var evaluator);
        // A Publish-only grant must not authorize Rollback -- they are distinct operator
        // operations (REQ-003: each elevated operation is independently policy-gated).
        evaluator.Allow(OperatorResourceType.StudioDraft, "own", OperatorOperation.Publish);

        var denied = await service.AuthorizeAsync(
            UserPrincipal(Alice), Alice, StudioAuthorizationOperation.Rollback, resourceOwnerId: Alice);
        Assert.False(denied.IsAllowed);

        evaluator.Allow(OperatorResourceType.StudioDraft, "own", OperatorOperation.Rollback);
        var allowed = await service.AuthorizeAsync(
            UserPrincipal(Alice), Alice, StudioAuthorizationOperation.Rollback, resourceOwnerId: Alice);
        Assert.True(allowed.IsAllowed);
    }

    [UnitTest]
    public async Task AuthorizeAsync_FlagOn_NonAdminOwnResource_GenerateUsesExecuteOperatorOperation()
    {
        // honua-server#3023: AI generation is elevated and maps to the Execute operator
        // operation. A Publish or Rollback grant must not authorize generation -- each
        // elevated operation is independently policy-gated.
        var service = BuildService(enabled: true, out var evaluator);
        evaluator.Allow(OperatorResourceType.StudioDraft, "own", OperatorOperation.Publish);
        evaluator.Allow(OperatorResourceType.StudioDraft, "own", OperatorOperation.Rollback);

        var denied = await service.AuthorizeAsync(
            UserPrincipal(Alice), Alice, StudioAuthorizationOperation.Generate, resourceOwnerId: Alice);
        Assert.False(denied.IsAllowed);
        Assert.True(denied.IsElevated);
        Assert.Equal(StudioAuthorizationService.ElevatedGrantRequiredCode, denied.Code);

        evaluator.Allow(OperatorResourceType.StudioDraft, "own", OperatorOperation.Execute);
        var allowed = await service.AuthorizeAsync(
            UserPrincipal(Alice), Alice, StudioAuthorizationOperation.Generate, resourceOwnerId: Alice);
        Assert.True(allowed.IsAllowed);
        Assert.True(allowed.IsElevated);
    }

    [UnitTest]
    public async Task AuthorizeAsync_FlagOn_NonAdminCrossUser_ElevatedOperationAllowedWithDelegateGrant()
    {
        // A delegate scenario: an operator granted alice explicit publish rights on bob's
        // specific item without making alice the owner.
        var service = BuildService(enabled: true, out var evaluator);
        evaluator.Allow(OperatorResourceType.StudioDraft, "item-42", OperatorOperation.Publish);

        var decision = await service.AuthorizeAsync(
            UserPrincipal(Alice),
            Alice,
            StudioAuthorizationOperation.PublishRequest,
            resourceOwnerId: Bob,
            resourceId: "item-42");

        Assert.True(decision.IsAllowed);
        Assert.True(decision.IsElevated);
    }

    [UnitTest]
    public async Task AuthorizeAsync_FlagOn_NonAdminCrossUser_ElevatedOperationDeniedWithoutDelegateGrant()
    {
        var service = BuildService(enabled: true, out var evaluator);
        // A grant for a different item must not authorize this one.
        evaluator.Allow(OperatorResourceType.StudioDraft, "item-99", OperatorOperation.Publish);

        var decision = await service.AuthorizeAsync(
            UserPrincipal(Alice),
            Alice,
            StudioAuthorizationOperation.PublishRequest,
            resourceOwnerId: Bob,
            resourceId: "item-42");

        Assert.False(decision.IsAllowed);
        Assert.True(decision.IsElevated);
        Assert.Equal(StudioAuthorizationService.CrossUserDeniedCode, decision.Code);
    }

    [UnitTest]
    public void IsAdmin_ReturnsTrueOnlyForAdminRole()
    {
        var service = BuildService(enabled: true, out _);
        Assert.True(service.IsAdmin(AdminPrincipal()));
        Assert.False(service.IsAdmin(UserPrincipal(Alice)));
    }

    [UnitTest]
    public void IsAdmin_RecognizesConfiguredOidcAdminRoleAlias()
    {
        // honua-server#3001 follow-up (P1): IsAdmin must recognize the same Oidc:AdminRoles
        // aliases (for example "administrator") that OidcAuthenticationExtensions.AddOidcAuthorization
        // uses to widen AdminPolicy/AdminPolicyAlias/the Temporal-* policies -- not just the
        // literal "admin" role -- so an OIDC admin authenticated under an alias role is never
        // incorrectly scoped to ownership.
        var service = BuildService(enabled: true, out _, adminRoles: ["admin", "administrator"]);
        var aliasedAdmin = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "oidc-admin-1"),
                new Claim(ClaimTypes.Role, "administrator"),
            ],
            authenticationType: "Test"));

        Assert.True(service.IsAdmin(aliasedAdmin));
    }

    [UnitTest]
    public void IsAdmin_UnconfiguredAlias_NotRecognized()
    {
        var service = BuildService(enabled: true, out _, adminRoles: ["admin", "administrator"]);
        var unrelatedRole = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "user-1"),
                new Claim(ClaimTypes.Role, "superuser"),
            ],
            authenticationType: "Test"));

        Assert.False(service.IsAdmin(unrelatedRole));
    }

    [UnitTest]
    public void IsAdmin_LiteralAdminRoleAlwaysRecognized_EvenWhenAliasListOmitsIt()
    {
        // The literal "admin" role must never regress even if a deployment configures
        // Oidc:AdminRoles to a list that (accidentally or otherwise) omits it -- every other
        // admin check on the platform (AdminApiKeyPermission, AdminSession) always recognizes
        // the literal role unconditionally.
        var service = BuildService(enabled: true, out _, adminRoles: ["administrator"]);
        Assert.True(service.IsAdmin(AdminPrincipal()));
    }

    [UnitTest]
    public async Task AuthorizeAsync_FlagOn_AliasedAdminRole_AllowedRegardlessOfOwnership()
    {
        var service = BuildService(enabled: true, out _, adminRoles: ["admin", "administrator"]);
        var aliasedAdmin = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "oidc-admin-1"),
                new Claim(ClaimTypes.Role, "administrator"),
            ],
            authenticationType: "Test"));

        var decision = await service.AuthorizeAsync(
            aliasedAdmin, "oidc-admin-1", StudioAuthorizationOperation.DeleteDraft, resourceOwnerId: Bob);

        Assert.True(decision.IsAllowed);
        Assert.False(decision.IsElevated);
    }

    [UnitTest]
    public void ResolveCallerId_UsesSchemeIssuerAndSubject()
    {
        var service = BuildService(enabled: true, out _);
        Assert.Equal(
            "oidc:subject:https%3A%2F%2Fissuer-a.example:alice",
            service.ResolveCallerId(OidcPrincipal(Alice, "https://issuer-a.example")));
        Assert.Null(service.ResolveCallerId(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    [Theory]
    [InlineData("AzureAd")]
    [InlineData("Google")]
    [InlineData("Oidc")]
    [InlineData("Okta")]
    [InlineData("Auth0")]
    public async Task ResolveCallerId_ValidatedOidcProviderScheme_PreservesOwnerSelfService(
        string authenticationType)
    {
        var service = BuildService(enabled: true, out _);
        var principal = OidcPrincipal(
            "provider-user",
            "https://provider.example",
            authenticationType);
        var callerId = service.ResolveCallerId(principal);

        callerId.Should().Be(
            "oidc:subject:https%3A%2F%2Fprovider.example:provider-user");
        var decision = await service.AuthorizeAsync(
            principal,
            callerId,
            StudioAuthorizationOperation.UpdateDraft,
            callerId);
        decision.IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    public async Task ResolveCallerId_TwoIssuersWithSameSubject_RemainDistinctAndCrossOwnerFailsClosed()
    {
        var service = BuildService(enabled: true, out _);
        var issuerA = OidcPrincipal("shared-subject", "https://issuer-a.example");
        var issuerB = OidcPrincipal("shared-subject", "https://issuer-b.example");
        var ownerA = service.ResolveCallerId(issuerA);
        var ownerB = service.ResolveCallerId(issuerB);

        ownerA.Should().NotBeNull().And.NotBe(ownerB);
        (await service.AuthorizeAsync(
            issuerA,
            ownerA,
            StudioAuthorizationOperation.UpdateDraft,
            ownerA)).IsAllowed.Should().BeTrue("the same issuer and subject own their resource");
        var crossIssuer = await service.AuthorizeAsync(
            issuerB,
            ownerB,
            StudioAuthorizationOperation.UpdateDraft,
            ownerA);
        crossIssuer.IsAllowed.Should().BeFalse();
        crossIssuer.Code.Should().Be(StudioAuthorizationService.CrossUserDeniedCode);

        var legacyBareOwner = await service.AuthorizeAsync(
            issuerA,
            ownerA,
            StudioAuthorizationOperation.UpdateDraft,
            "shared-subject");
        legacyBareOwner.IsAllowed.Should().BeFalse("legacy bare subjects must not bind to a new issuer opportunistically");
    }

    [UnitTest]
    public void ResolveCallerId_ApiKeyUsesSchemeAndImmutableKeyId()
    {
        var service = BuildService(enabled: true, out _);
        var keyId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("auth_type", "admin-api-key"),
                new Claim("api_key_id", keyId.ToString("D")),
                new Claim("api_key_name", "mutable-display-name"),
            ],
            authenticationType: "ApiKey"));

        service.ResolveCallerId(principal).Should().Be($"admin-api-key:api-key:{keyId:D}");
    }

    [UnitTest]
    public void ResolveCallerId_SubjectWithoutValidatedIssuerFailsClosed()
    {
        var service = BuildService(enabled: true, out _);
        service.ResolveCallerId(UserPrincipal(Alice)).Should().BeNull();
    }

    [UnitTest]
    public void ResolveCallerId_OperatorBearerUsesUpstreamIssuerProvenanceNotWrapperIssuer()
    {
        var service = BuildService(enabled: true, out _);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "shared-subject"),
                new Claim("auth_type", "oidc"),
                new Claim(IdentityProtocolProvenance.ClaimType, IdentityProtocolProvenance.Oidc),
                new Claim("iss", "honua-operator-bearer"),
                new Claim("honua_identity_issuer", "https://issuer-a.example"),
            ],
            authenticationType: "OperatorBearer"));

        service.ResolveCallerId(principal).Should().Be(
            "oidc:subject:https%3A%2F%2Fissuer-a.example:shared-subject");
    }

    [UnitTest]
    public void ResolveCallerId_OperatorBearerWithoutUpstreamIssuerProvenanceFailsClosed()
    {
        var service = BuildService(enabled: true, out _);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "shared-subject"),
                new Claim("auth_type", "oidc"),
                new Claim(IdentityProtocolProvenance.ClaimType, IdentityProtocolProvenance.Oidc),
                new Claim("iss", "honua-operator-bearer"),
            ],
            authenticationType: "OperatorBearer"));

        service.ResolveCallerId(principal).Should().BeNull(
            "the wrapper issuer is not the upstream subject namespace");
    }

    [UnitTest]
    public async Task ResolveCallerId_DirectSamlWithoutIssuer_PreservesOwnResourceAuthorization()
    {
        var service = BuildService(enabled: true, out _);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "saml-user-1"),
            new Claim("auth_type", "saml"),
            new Claim(IdentityProtocolProvenance.ClaimType, IdentityProtocolProvenance.Saml),
            new Claim(ClaimTypes.Role, "creator"),
        ],
        authenticationType: "saml"));

        var callerId = service.ResolveCallerId(principal);
        callerId.Should().Be("saml:subject:-:saml-user-1");
        var ownDecision = await service.AuthorizeAsync(
            principal,
            callerId,
            StudioAuthorizationOperation.UpdateDraft,
            callerId);
        ownDecision.IsAllowed.Should().BeTrue();
    }

    private static StudioAuthorizationService BuildService(
        bool enabled,
        out FakeOperatorAuthorizationEvaluator evaluator,
        string[]? adminRoles = null)
    {
        evaluator = new FakeOperatorAuthorizationEvaluator();
        var options = new StaticOptionsMonitor<StudioEndUserAuthorizationOptions>(new StudioEndUserAuthorizationOptions { Enabled = enabled });
        var adminRoleOptions = new StaticOptionsMonitor<AdminRoleOptions>(new AdminRoleOptions { AdminRoles = adminRoles ?? [] });
        return new StudioAuthorizationService(evaluator, options, adminRoleOptions);
    }

    private static ClaimsPrincipal AdminPrincipal()
        => new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "admin-1"),
                new Claim(ClaimTypes.Role, "admin"),
            ],
            authenticationType: "Test"));

    private static ClaimsPrincipal UserPrincipal(string userId)
        => new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(ClaimTypes.Role, "creator"),
            ],
            authenticationType: "Test"));

    private static ClaimsPrincipal OidcPrincipal(
        string subject,
        string issuer,
        string authenticationType = "oidc")
        => new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, subject),
                new Claim("sub", subject),
                new Claim("iss", issuer),
                new Claim("auth_type", "oidc"),
                new Claim(IdentityProtocolProvenance.ClaimType, IdentityProtocolProvenance.Oidc),
                new Claim(ClaimTypes.Role, "creator"),
            ],
            authenticationType));

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<T, string?> listener) => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();
            public void Dispose() { }
        }
    }

    /// <summary>
    /// Records every evaluated request and grants access only for explicitly allow-listed
    /// (resourceType, resourceId, operation) tuples -- deny-by-default, matching the real
    /// evaluator's posture for a principal with no matching grant.
    /// </summary>
    private sealed class FakeOperatorAuthorizationEvaluator : IOperatorAuthorizationEvaluator
    {
        private readonly HashSet<(OperatorResourceType, string, OperatorOperation)> _allowed = [];

        public List<OperatorAuthorizationRequest> Requests { get; } = [];

        public void Allow(OperatorResourceType resourceType, string resourceId, OperatorOperation operation)
            => _allowed.Add((resourceType, resourceId, operation));

        public Task<AccessDecision> EvaluateAsync(
            ClaimsPrincipal principal,
            OperatorAuthorizationRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var key = (request.ResourceType, request.ResourceId ?? string.Empty, request.Operation);
            return Task.FromResult(_allowed.Contains(key)
                ? AccessDecision.Allowed()
                : AccessDecision.Forbidden("no matching grant"));
        }
    }
}
