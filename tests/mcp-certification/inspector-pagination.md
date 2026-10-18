# Public Inspector pagination observation

This server-owned helper forwards the installed Inspector's HTTP requests and
candidate responses without creating or editing MCP methods, IDs, cursors,
sessions or resources. The canonical producer invokes the public Inspector CLI
against its loopback observer endpoint; the observer forwards only to the exact
configured `/mcp` endpoint. It rejects redirects and never records credentials.

A proof requires at least two actual pages, matching response/request IDs, a
stable session context, every opaque continuation cursor handed to the next
request exactly once, a final page without continuation, unique resource URIs,
and the Inspector's final resource list matching the entire observed chain.
A single-page result is not a cursor-traversal proof. Captured resource URIs and
session IDs are hashed in retained proof. Error text is fixed and contains no
upstream body. Requests/pages, bytes and observation time are bounded; all owned
connections close on every exit.

The controls run the installed public CLI from the canonical producer's
immutable dependency lock at `372d9ad1366a5fa28165e7ebbc7481e6e1ca13c4`.
That lock installs Inspector **2.10.1**. The separate release requirements still
name **2.3.0**; these controls do not qualify that version, change its requirement,
or resolve the existing release181 catalog mismatch. Scripted peer successes
and negative controls are explicitly labelled `candidateQualified: false`.
The actual candidate must supply the pages; controls never become candidate
receipts. Missing or mismatched immutable helper bytes remain nonpassing.

This implements pagination observation only. Inspector ping addressability,
unknown-tool errors and cancellation proof compatibility remain open under the
canonical producer's existing work. A new server image alone cannot clear them.
