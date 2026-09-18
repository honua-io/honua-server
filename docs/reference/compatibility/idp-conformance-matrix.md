---
type: reference
title: "SCIM / SAML IdP Conformance Matrix"
description: "Which SAML 2.0 and SCIM 2.0 capabilities are supported, partial or unsupported against Okta, Entra ID, Auth0 and PingFederate, with the per-provider attribute settings and quirks."
resource: "honua://capability/identity.scim"
resources:
  - "honua://capability/identity.saml"
---
# SCIM / SAML IdP Conformance Matrix

This matrix records the supported / partial / unsupported status of Honua's identity surfaces against the four major identity
providers, plus the known per-provider quirks operators should account for.

Honua provisions and authenticates into a **single durable role store**: SCIM,
SAML, and OIDC all land in the same identity/role model rather than parallel stores. SCIM
provisioning is RFC 7643/7644-conformant and provider-agnostic; SAML attribute mapping is
the main per-provider variable and is exercised by the conformance matrix tests.

## SAML 2.0 SSO

| Capability | Okta | Entra ID | Auth0 | PingFederate |
|---|---|---|---|---|
| Signed assertion consumption (RSA/ECDSA, SHA-256/384/512, Exclusive C14N) | Supported | Supported | Supported | Supported |
| AudienceRestriction / Issuer / NotBefore-NotOnOrAfter enforcement | Supported | Supported | Supported | Supported |
| NameID → subject | Supported | Supported | Supported | Supported |
| Email / display-name attribute mapping | Supported | Supported | Supported | Supported |
| Role/group attribute mapping | Supported | Supported | Supported | Supported |
| Default-role fallback when no role claim is present | Supported | Supported | Supported | Supported |
| Single Logout (SLO) — IdP-initiated, HTTP-POST, signed `LogoutRequest` | Supported | Supported | Supported | Supported |
| Single Logout (SLO) — SP-initiated / HTTP-Redirect binding | Partial (deferred) | Partial (deferred) | Partial (deferred) | Partial (deferred) |

### Per-provider attribute configuration

Set `Saml:RoleAttribute`, `Saml:EmailAttribute`, and `Saml:DisplayNameAttribute` to match
the attribute `Name` your IdP emits:

| Provider | Role attribute | Email attribute | Display-name attribute |
|---|---|---|---|
| Okta | `groups` (or your group-claim name) | `email` | `displayName` |
| Entra ID | `http://schemas.microsoft.com/ws/2008/06/identity/claims/role` | `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress` | `http://schemas.microsoft.com/identity/claims/displayname` |
| Auth0 | namespaced custom claim, e.g. `https://your-domain/saml/roles` | `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress` | `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name` |
| PingFederate | `memberOf` (directory attribute) | `mail` | `cn` |

The validator resolves an attribute by both its `Name` and `FriendlyName`, so either form
works when an IdP emits both.

### Single Logout (SLO) configuration

Set `Saml:SingleLogoutServiceUrl` to the SP's own `/saml/slo` endpoint to advertise a
`SingleLogoutService` (HTTP-POST binding) in SP metadata. When the IdP front-channels a signed
`LogoutRequest` there, Honua verifies the signature against `Saml:IdpSigningCertificate`,
terminates the local admin session (store record + cookie), and emits a `LogoutResponse`. Set
`Saml:IdpSingleLogoutServiceUrl` to relay that `LogoutResponse` back to the IdP via an
auto-submitting HTTP-POST form; leave it unset to return the `LogoutResponse` directly. Only
signed `LogoutRequest`s are honored — unsigned or forged logout requests are rejected so they
cannot terminate a session.

Admin OIDC logout and SAML SLO require successful removal of the shared session record.
If the configured session store is unavailable, they return a retryable HTTP `503`,
preserve the session cookie, and do not emit a successful logout response. The session
has not been revoked: retry logout after recovery. A successful retry deletes the
shared record before clearing the cookie, including for other replicas.

### Known quirks

- **Okta** — group membership is delivered as a multi-valued `groups` attribute; map it to
  `RoleAttribute`. Okta defaults the NameID to the user's email/login.
- **Entra ID** — emits long Microsoft/WS-\* claim-type URIs; the role claim only appears when
  app roles or group claims are explicitly configured on the enterprise application. Group
  claims may arrive as group **object IDs** rather than names unless the app is configured to
  emit display names.
- **Auth0** — custom claims must be **namespaced** (a non-namespaced custom claim is dropped
  by Auth0); configure a SAML mapping that emits roles under a namespaced attribute.
- **PingFederate** — typically maps straight from directory attributes; `memberOf` values are
  often full LDAP DNs, so RBAC role names should match the DN or be normalized by the
  operator's role-mapping configuration.

## SCIM 2.0 provisioning

`Scim:OidcIssuer` is required whenever `Scim:BearerToken` enables provisioning and must
match the exact OIDC `iss` value for the IdP connected to this SCIM endpoint. Honua
persists that trusted configuration with each SCIM `externalId`, forming an
issuer-plus-subject key, so identical `sub` values from different configured issuers
never share role membership. User creation therefore requires the IdP to send its OIDC
subject in the SCIM `externalId` field.

| Capability | Okta | Entra ID | Auth0 | PingFederate |
|---|---|---|---|---|
| User create / replace (PUT) | Supported | Supported | Supported | Supported |
| User deactivate (`active:false`) / delete | Supported | Supported | Supported | Supported |
| User PATCH (active / roles) | Supported | Supported | Supported | Supported |
| Group create / replace / delete | Supported | Supported | Supported | Supported |
| Group membership PATCH (add / remove) | Supported | Supported | Supported | Supported |
| Filtering + pagination (`filter`, `startIndex`, `count`) | Supported | Supported | Supported | Supported |
| Bearer-token authentication | Supported | Supported | Supported | Supported |
| Discovery documents (`/ServiceProviderConfig`, `/ResourceTypes`, `/Schemas`) | Supported | Supported | Supported | Supported |

### Known quirks

- **Okta** — provisions users then assigns groups via group-membership PATCH; expects
  `application/scim+json` and RFC 7644 `Operations` PATCH semantics (both supported).
- **Entra ID** — drives membership changes through group PATCH `add`/`remove` value arrays
  and probes `/scim/v2/Users?filter=userName eq "..."` before create; both are supported.
- **Auth0** — provisions users via its SCIM client; group-to-role mapping flows through the
  group display name (each SCIM group maps to a Honua role).
- **PingFederate** — uses standard RFC 7644 PUT/PATCH; no Honua-specific deviation observed.

## Not yet supported

- SP-initiated SAML logout (Honua generating and signing its own `LogoutRequest`) and the
  HTTP-Redirect SLO binding (with its query-string signature scheme). IdP-initiated,
  HTTP-POST, signed Single Logout is supported today.
