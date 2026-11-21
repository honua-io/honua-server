---
type: capability
title: "mTLS Client-Certificate Authentication"
description: "Native client-certificate (mTLS) authentication: trust profiles, issuer/chain validation with CRL/OCSP revocation, and certificate-to-principal mapping."
resource: "honua://capability/identity.mtls-client-certificate"
tags: [capability, identity, enterprise]
---
<!-- GENERATED FILE - DO NOT EDIT. Regenerate with scripts/ci/generate-capability-concepts.py -->

# mTLS Client-Certificate Authentication

Native client-certificate (mTLS) authentication: trust profiles, issuer/chain validation with CRL/OCSP revocation, and certificate-to-principal mapping.

| | |
| --- | --- |
| Capability key | `identity.mtls-client-certificate` |
| Category | Identity |
| Edition | Enterprise |
| Status | experimental |
| Surface maturity | 13 experimental |
| Registry entries | 13 |
| Proving tests | 35 |

The facts above come from the server's capability registry and capability matrix, which are generated from the server's own route catalog and test evidence rather than from prose.

## Documented in

- [Terminate TLS and require client certificates](../../guides/secure/tls-and-mtls.md)

## Availability

Status is **experimental**. Not every experimental capability is on by default, so read `identity.mtls-client-certificate` in `GET /api/v1/capabilities/manifest` before calling its routes. `available: true` means the routes are served. `available: false` with `reasonCode: experimental-disabled` means they return 404 until `Capabilities:Experimental:identity.mtls-client-certificate:Enabled` is `true` (or the global `Capabilities:Experimental:Enabled`). Set it in configuration or as the container environment variable `Capabilities__Experimental__identity.mtls-client-certificate__Enabled`; a POSIX shell cannot `export` a name containing `.` or `-`. Edition licensing still applies once it is enabled.
