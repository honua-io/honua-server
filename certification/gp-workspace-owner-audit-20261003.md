# GP workspace owner routing audit

The job ownership update in #5382 keeps the established named workspace owner
key separately from the durable job lifecycle owner. This bounded audit compares
implementation `f984c1a74` with integrated trunk
`f7679d1ee40097c26dbea6afbbc722bc2d501f92`.

Only `GeoprocessingDispatchJobExecutor.cs` changes within the operation matrix's
catalog source roots. Its workspace resolver uses the captured workspace owner
key when present and retains `Audit.RequestedBy` for older records. The existing
anonymous fallback, label, tenant scope, retention, overwrite routing, artifact
publication and error handling remain the same. Jobs without a requested
workspace retain their existing dispatch path.

Catalog membership, declared entry points, individual executors and every
semantic evidence file referenced by the 98 operation rows are unchanged. The
local matrix checks confirm membership, entry points and all evidence method-body
digests. The 82 job, 4 protocol and 12 workflow verdicts retain their existing
semantic receipt scope; their status and the shared runtime gaps remain unchanged.

Four dispatcher regressions failed at the retained-workspace publication
assertion before separate owner routing metadata and then passed. They cover
default and tenant-scoped workspaces, middleware-bound and ordinary principals,
and overwrite publication into the retained workspace. The generated job-store
JSON round trip preserves both owner fields. All 333 tests in the existing tile
lifecycle, GP lifecycle, identity capture, dispatcher and gRPC classes passed.
These unit checks establish routing and persistence metadata behavior within their
existing fixture scope. The per-operation semantic receipts and existing runtime
certification obligations continue to supply the operation matrix's evidence.

The refreshed content digest records this bounded source review. The unchanged
architecture checks enforce that digest and the existing evidence bodies.
