// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Security;
using Honua.Core.Features.Studio.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Studio.Services;

/// <inheritdoc cref="IStudioAuthorizationService" />
public sealed class StudioAuthorizationService : IStudioAuthorizationService
{
    /// <summary>
    /// Sentinel operator-grant resource id meaning "any resource the caller owns". Lets an
    /// operator provision a single self-service grant
    /// (<c>Service=StudioDraft, Layer=own, Operation=...</c>) that authorizes every draft/item a
    /// non-admin principal owns, without pre-provisioning a grant per resource id. See
    /// <see cref="OperatorResourceType.StudioDraft"/>.
    /// </summary>
    internal const string OwnResourceSentinel = "own";

    private const string AdminRole = "admin";
    private const string IdentityIssuerClaimType = "honua_identity_issuer";

    /// <summary>Denial code: the flag is off, so only admins may use the Studio lifecycle surface.</summary>
    public const string EndUserModeDisabledCode = "studio_authorization/end_user_mode_disabled";

    /// <summary>Denial code: the caller does not own the target resource and it is not publicly readable.</summary>
    public const string CrossUserDeniedCode = "studio_authorization/cross_user_denied";

    /// <summary>Denial code: the caller is not authenticated.</summary>
    public const string AuthenticationRequiredCode = "studio_authorization/authentication_required";

    /// <summary>Denial code: an elevated operation (publish-request/rollback) has no matching operator grant.</summary>
    public const string ElevatedGrantRequiredCode = "studio_authorization/elevated_grant_required";

    /// <summary>
    /// Denial code: the caller's OAuth access-token scopes do not reach this operation. Distinct
    /// from the grant-denial codes so an operator can tell a too-narrow token apart from a
    /// missing grant (honua-server#2851/#3431).
    /// </summary>
    public const string ScopeDeniedCode = "studio_authorization/scope_denied";

    private readonly IOperatorAuthorizationEvaluator _evaluator;
    private readonly IOperatorScopeAuthorizer _scopeAuthorizer;
    private readonly IOptionsMonitor<StudioEndUserAuthorizationOptions> _options;
    private readonly IOptionsMonitor<AdminRoleOptions> _adminRoleOptions;

    /// <summary>Initializes a new Studio authorization service.</summary>
    public StudioAuthorizationService(
        IOperatorAuthorizationEvaluator evaluator,
        IOperatorScopeAuthorizer scopeAuthorizer,
        IOptionsMonitor<StudioEndUserAuthorizationOptions> options,
        IOptionsMonitor<AdminRoleOptions> adminRoleOptions)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(scopeAuthorizer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(adminRoleOptions);
        _evaluator = evaluator;
        _scopeAuthorizer = scopeAuthorizer;
        _options = options;
        _adminRoleOptions = adminRoleOptions;
    }

    /// <inheritdoc />
    public bool IsEndUserAuthorizationEnabled => _options.CurrentValue.Enabled;

    /// <inheritdoc />
    public bool IsAdmin(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        // The literal "admin" role is always recognized regardless of configuration -- this
        // matches every other admin check on the platform (AdminApiKeyPermission, AdminSession,
        // the API-key handler's role stamping) and must never regress even if a deployment
        // configures Oidc:AdminRoles to a list that omits it.
        if (principal.IsInRole(AdminRole))
        {
            return true;
        }

        // Also recognize configured OIDC admin-role aliases (for example "administrator"), the
        // same Oidc:AdminRoles-driven set that OidcAuthenticationExtensions.AddOidcAuthorization
        // widens AdminPolicy/AdminPolicyAlias/the Temporal-* policies with. See
        // AdminRoleOptions for why this reads the same config key rather than referencing that
        // type directly (Honua.Core cannot depend on Honua.Hosting).
        var aliases = _adminRoleOptions.CurrentValue.AdminRoles;
        if (aliases is null)
        {
            return false;
        }

        return aliases.Any(alias => !string.IsNullOrWhiteSpace(alias) && principal.IsInRole(alias));
    }

    /// <inheritdoc />
    public string? ResolveCallerId(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Identity is not { IsAuthenticated: true } identity)
        {
            return null;
        }

        // API-key display names are mutable and non-unique. Only the immutable key id is a
        // durable ownership key, qualified by the validated authentication scheme.
        var isApiKeyIdentity = (FrameworkAuthenticationIdentity.IsApiKey(identity.AuthenticationType)
                || string.Equals(
                    identity.AuthenticationType,
                    FrameworkAuthenticationIdentity.JobSecurityContextAuthenticationType,
                    StringComparison.Ordinal))
            && FrameworkAuthenticationIdentity.HasApiKeyCredentialKind(principal);
        var apiKeyValue = isApiKeyIdentity
            ? NormalizeIdentityComponent(principal.FindFirst("api_key_id")?.Value)
            : null;
        if (apiKeyValue is not null)
        {
            var apiKeyScheme = string.Equals(
                identity.AuthenticationType,
                FrameworkAuthenticationIdentity.JobSecurityContextAuthenticationType,
                StringComparison.Ordinal)
                ? "admin-api-key"
                : NormalizeIdentityComponent(principal.FindFirst("auth_type")?.Value);
            return Guid.TryParse(apiKeyValue, out var apiKeyId)
                && apiKeyScheme is not null
                ? $"{apiKeyScheme.ToLowerInvariant()}:api-key:{apiKeyId:D}"
                : null;
        }

        // A bare OIDC subject is not globally unique: two trusted issuers can legally mint the
        // same sub. Require the validated issuer claim for OIDC and include it with the auth
        // scheme in the ownership key. SAML sessions do not yet carry durable entity-id
        // provenance, so preserve their explicit issuer-optional namespace rather than rejecting
        // an otherwise validated SAML principal. Legacy bare owner ids still compare unequal and
        // fail closed; they are never opportunistically rebound to whichever caller asks first.
        var subject = NormalizeIdentityComponent(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value)
            ?? NormalizeIdentityComponent(principal.FindFirst("sub")?.Value);
        // An operator bearer is a Honua-signed wrapper around an already validated upstream
        // session. Its registered `iss` is therefore the wrapper signing authority, not the
        // namespace of the upstream subject. OperatorBearerTokenService carries the validated
        // upstream issuer in a private provenance claim; require that claim for wrappers and
        // never fall back to the wrapper issuer. Direct OIDC principals continue to use their
        // validated `iss` and deliberately ignore any untrusted lookalike provenance claim.
        var isOperatorBearer = string.Equals(
            identity.AuthenticationType,
            FrameworkAuthenticationIdentity.OperatorBearerAuthenticationType,
            StringComparison.Ordinal);
        var protocol = FrameworkAuthenticationIdentity.ResolveDurableSubjectScheme(
                identity.AuthenticationType)
            ?? IdentityProtocolProvenance.Resolve(principal);
        var isOidc = string.Equals(protocol, IdentityProtocolProvenance.Oidc, StringComparison.Ordinal);
        var isSaml = string.Equals(protocol, IdentityProtocolProvenance.Saml, StringComparison.Ordinal);
        var isFrameworkSubject = FrameworkAuthenticationIdentity.IsDurableSubjectScheme(protocol);
        var issuer = isOperatorBearer
            ? isOidc
                ? NormalizeIdentityComponent(principal.FindFirst(IdentityIssuerClaimType)?.Value)
                : null
            : isOidc
                ? NormalizeIdentityComponent(principal.FindFirst("iss")?.Value)
                : null;
        if (subject is null
            || (!isOidc && !isSaml && !isFrameworkSubject)
            || (isOidc && issuer is null))
        {
            return null;
        }

        var issuerNamespace = issuer is null ? "-" : Uri.EscapeDataString(issuer);
        return $"{protocol}:subject:{issuerNamespace}:{Uri.EscapeDataString(subject)}";
    }

    private static string? NormalizeIdentityComponent(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    /// <inheritdoc />
    public async Task<StudioAuthorizationDecision> AuthorizeAsync(
        ClaimsPrincipal principal,
        string? callerId,
        StudioAuthorizationOperation operation,
        string? resourceOwnerId,
        bool isPubliclyReadable = false,
        string? resourceId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);

        // OAuth 2.1 scope narrowing (honua-server#2851, regression guard honua-server#3431).
        // The role/grant model below decides what the principal MAY do; when the caller
        // authenticated with a bearer token its scopes can only narrow that -- never widen it.
        //
        // This runs ahead of the admin bypass deliberately. It is the same unconditional
        // ceiling GeoprocessingJobAuthorizer.EnsureAuthorizedAsync applies (that method has no
        // admin short-circuit either), so a narrow-scoped token cannot mutate a draft merely
        // because its principal also carries the admin role. The Studio MCP draft tools used to
        // reach that authorizer through IGeoprocessingJobService.EnsureCallerAuthorizedAsync;
        // now that they authorize through this service instead, enforcing the scope ceiling
        // here is what keeps the MCP surface from losing scope narrowing altogether.
        //
        // Non-OAuth principals (X-API-Key, interactive sessions, dev-bypass) report NotGoverned
        // and pass through untouched.
        var scopeDecision = _scopeAuthorizer.Evaluate(
            principal, OperatorResourceType.StudioDraft, MapToScopeOperation(operation));
        if (!scopeDecision.IsAllowed)
        {
            return StudioAuthorizationDecision.Deny(
                ScopeDeniedCode,
                scopeDecision.Reason
                    ?? $"The access token's scopes do not permit '{operation}' on Studio drafts.");
        }

        // Admins always have full, unscoped access -- unchanged before and after #3001, and
        // independent of the feature flag.
        if (IsAdmin(principal))
        {
            return StudioAuthorizationDecision.Allow();
        }

        if (!IsEndUserAuthorizationEnabled)
        {
            return StudioAuthorizationDecision.Deny(
                EndUserModeDisabledCode,
                "Studio package lifecycle operations require the admin role.");
        }

        if (principal.Identity is not { IsAuthenticated: true } || string.IsNullOrWhiteSpace(callerId))
        {
            return StudioAuthorizationDecision.Deny(
                AuthenticationRequiredCode,
                "Authentication is required for Studio package lifecycle operations.");
        }

        var isElevated = operation is StudioAuthorizationOperation.PublishRequest
            or StudioAuthorizationOperation.Rollback
            or StudioAuthorizationOperation.Generate;
        var isRead = operation is StudioAuthorizationOperation.ReadDraft
            or StudioAuthorizationOperation.ReadContentItem
            or StudioAuthorizationOperation.ListOwn;
        // Fail closed on an ownerless *existing* resource: owner_id is a nullable column
        // (legacy rows created before honua-server#3001's ownership migration may still be
        // unbackfilled), so treating a null owner as "owned by whoever asks" would let any
        // authenticated caller claim it. A null owner here means "no owner assigned yet" --
        // only an admin may act on it until an owner is assigned. Endpoints that create a
        // brand-new resource never reach this method with a null resourceOwnerId; they pass
        // the actual (possibly just-created) owner id of an existing resource in every call
        // site (see StudioPackageEndpoints.EnsureAuthorizedAsync call sites).
        var isOwn = resourceOwnerId is not null && string.Equals(resourceOwnerId, callerId, StringComparison.Ordinal);

        if (!isElevated)
        {
            // Baseline tier: ownership (or public-read visibility) alone authorizes the
            // operation. No operator grant is required -- the flag itself is the widening
            // switch for REQ-002's "non-admin sessions can complete the draft->version flow on
            // their own items".
            if (isOwn || (isRead && isPubliclyReadable))
            {
                return StudioAuthorizationDecision.Allow();
            }

            return StudioAuthorizationDecision.Deny(
                CrossUserDeniedCode,
                "The caller does not own this Studio resource.");
        }

        // Elevated tier (REQ-003): publish-request and rollback always require a matching
        // StudioDraft operator grant, evaluated through the platform's existing role/grant
        // infrastructure rather than a parallel mechanism -- ownership alone never suffices, and
        // (unlike the baseline tier) a cross-user caller is not rejected before the grant check
        // runs, since an operator-provisioned delegate grant is scoped to the concrete resource
        // id regardless of who owns it. A grant scoped to the "own" sentinel authorizes every
        // resource the caller owns; a grant scoped to the concrete resourceId (or the "*"
        // wildcard) authorizes an operator-provisioned delegate, independent of ownership.
        var operatorOperation = operation switch
        {
            StudioAuthorizationOperation.PublishRequest => OperatorOperation.Publish,
            StudioAuthorizationOperation.Generate => OperatorOperation.Execute,
            _ => OperatorOperation.Rollback,
        };

        if (isOwn && await HasOperatorGrantAsync(principal, operatorOperation, OwnResourceSentinel, cancellationToken).ConfigureAwait(false))
        {
            return StudioAuthorizationDecision.Allow(elevated: true);
        }

        if (resourceId is not null
            && await HasOperatorGrantAsync(principal, operatorOperation, resourceId, cancellationToken).ConfigureAwait(false))
        {
            return StudioAuthorizationDecision.Allow(elevated: true);
        }

        if (!isOwn)
        {
            return StudioAuthorizationDecision.Deny(
                CrossUserDeniedCode,
                "The caller does not own this Studio resource and holds no delegate operator grant for it.",
                elevated: true);
        }

        return StudioAuthorizationDecision.Deny(
            ElevatedGrantRequiredCode,
            $"'{operation}' requires a StudioDraft '{operatorOperation}' operator grant.",
            elevated: true);
    }

    /// <summary>
    /// Maps a Studio lifecycle operation onto the operator operation whose OAuth scope governs
    /// it. Mutations map to <see cref="OperatorOperation.Create"/> and reads to
    /// <see cref="OperatorOperation.Read"/> -- the only two operations the Studio MCP tools ever
    /// passed to the geoprocessing authorizer before this service owned the check, so scope
    /// coverage is restored exactly rather than widened.
    /// <para>
    /// Draft edits deliberately do NOT map to <see cref="OperatorOperation.Update"/> or
    /// <see cref="OperatorOperation.Delete"/>: <c>OperatorScopeCatalog.ScopeOperations</c> maps
    /// neither to any scope except the full scope, so routing edits through them would fail
    /// every normally delegated token -- the same regression honua-server#3046 caught on the
    /// geoprocessing path.
    /// </para>
    /// </summary>
    private static OperatorOperation MapToScopeOperation(StudioAuthorizationOperation operation)
        => operation switch
        {
            StudioAuthorizationOperation.ReadDraft
                or StudioAuthorizationOperation.ReadContentItem
                or StudioAuthorizationOperation.ListOwn
                or StudioAuthorizationOperation.ValidateDraft => OperatorOperation.Read,
            StudioAuthorizationOperation.PublishRequest => OperatorOperation.Publish,
            StudioAuthorizationOperation.Generate => OperatorOperation.Execute,
            StudioAuthorizationOperation.Rollback => OperatorOperation.Rollback,
            _ => OperatorOperation.Create,
        };

    private async Task<bool> HasOperatorGrantAsync(
        ClaimsPrincipal principal,
        OperatorOperation operation,
        string resourceId,
        CancellationToken cancellationToken)
    {
        var decision = await _evaluator.EvaluateAsync(
            principal,
            new OperatorAuthorizationRequest
            {
                ResourceType = OperatorResourceType.StudioDraft,
                ResourceId = resourceId,
                Operation = operation,
            },
            cancellationToken).ConfigureAwait(false);
        return decision.IsAllowed;
    }
}
