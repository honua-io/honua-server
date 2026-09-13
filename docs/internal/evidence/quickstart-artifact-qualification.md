# Quickstart artifact identity and qualification (internal)

Moved out of `get-started/quickstart.md`, where it ran to 36 lines before
step 1 and opened the customer quickstart with "This is not a qualified
2026.1 candidate". The pins a reader needs are stated on the page itself;
the qualification record is this.

## Artifact identity

The commands pin the anonymously published **pre-cut rehearsal** image
`ghcr.io/honua-io/honua-server@sha256:273b4c616e806b8ac2809946659986960a1803e55bda79d99db5f3955b6c30b9`
(Docker Desktop Linux containers; this journey selects `linux/amd64`, source `5a657b9eaed7cdeac915d584ad58c028a52ca61e`). Its
[registry manifest](https://ghcr.io/v2/honua-io/honua-server/manifests/sha256:273b4c616e806b8ac2809946659986960a1803e55bda79d99db5f3955b6c30b9)
is fetched by `docker pull` below. The control-plane package is
[honua-admin 0.1.8](https://pypi.org/project/honua-admin/0.1.8/); the data-plane
package is [honua-sdk 0.1.11](https://pypi.org/project/honua-sdk/0.1.11/).
The import step invokes Honua's `honua_ingest_dataset` MCP tool using the
published [MCP transport client 2.1.1](https://pypi.org/project/mcp/2.1.1/).

Download the [customer install manifest](https://honua.io/data/customer-install-manifest.json)
for all image and client identities, wheel hashes and direct downloads. Its Honua
client pins come from the release manifest; the server pin comes from the linked
successful pre-cut rehearsal. The [publication record](https://honua.io/data/customer-install-publication.json)
identifies the immutable release-repository source and SHA-256 of the public copy.
The GHCR manifest URL uses the OCI registry protocol; Docker handles its anonymous
bearer-token exchange. It does not require a GitHub account.

**This is not a qualified 2026.1 candidate.** After the cut, replace the server
and compatible client pins together from the signed release lock and repeat this
journey on a clean Windows machine in the Windows licensed lane. Link that
separate qualification record on [#4300](https://github.com/honua-io/honua-server/issues/4300).
Do not substitute the historical 2026.1 release or the moving candidate snapshot.

The [pre-cut Windows receipt](../../guides/deploy/evidence/windows-packages-4300.json)
records successful fresh-volume startup, anonymous denial, authenticated admin
access, import/publish/query, restart readback, container-recreation readback,
and scoped teardown with these packages. It used an existing Windows host with
a new installation directory and virtual environment, not a clean-machine RC
qualification.

The [documentation validation record](../../guides/deploy/evidence/customer-install-docs-4300.json) separately records
a Linux runtime replay of the updated commands, database and file-storage restore,
and native PowerShell syntax checks. It is not a clean-Windows qualification.

