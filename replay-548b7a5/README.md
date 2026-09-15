# #4577 replay on the re-pinned candidate (nightly-548b7a5)

Image `ghcr.io/honua-io/honua-server@sha256:29974ee7b722e3ae15c3b891024e5e70800f412188aeccf5ec3d32d9dac675c1`
(revision `548b7a5263da5a3f2381eb43f232687cdf92b0bf`, `honua.runtime.compilation=native-aot`, Production).
The pin contains #4578 (generateToken bridge) and #4789 (client_credentials exchange authority).

Same harness as the top-level README. Two changes to `boot.sh`, both needed for this image:

- Images after #4722 exit at boot without `Operations__SecretChannel__KeyRingCertificatePath`.
  `boot.sh` mounts `keyring/keyring.pfx` read-only; a throwaway self-signed PKCS#12 is enough:
  `openssl req -x509 -newkey rsa:2048 -nodes -keyout k.pem -out c.pem -days 7 -subj /CN=keyring &&
  openssl pkcs12 -export -inkey k.pem -in c.pem -out keyring/keyring.pfx -passout pass:` (mode 0644).
- `Authentication__PortalToken__OAuth2__EnableClientCredentials` defaults to `true`, so all three
  scripts run against one boot.

`compile-fn.sql` is byte-identical to the pin's `seed_metadata_v2_compat_snapshot()`.

| Script | Result |
|---|---|
| `replay.py` (generateToken) | 71/71 checks, 89 requests |
| `replay_client_credentials.py` | 12/12 checks, 42 requests |
| `replay_role_label.py` | `field-editor` and `read:` keys: client_credentials `unauthorized_client`, generateToken Esri 400, direct 403 |

client_credentials on the pin: the constrained keys (`read:<svc>`, `admin:read`, `ops:read`, unknown label)
all get HTTP 400 `unauthorized_client` with no token. A full-admin key (`admin:*`) gets a token that reads
the admin-only `protected-alpha` and `scoped-alpha` through both FeatureServer and OGC API Features.
Revoked, expired and invalid secrets get `invalid_client`.

Receipts contain only statuses, error codes, feature names and body hashes. They were scanned for the admin
password, key material and tokens (0 hits). The isolated stack and its database were destroyed afterwards.
