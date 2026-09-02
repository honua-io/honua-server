# WFS 2.0 best-effort transaction receipts

WFS 2.0 `TransactionResponse` has standard success totals plus per-feature results for inserts and
replaces, but it has no standard per-operation failure list for a best-effort
`rollbackOnFailure="false"` transaction. Honua preserves the standard response members and, only
when at least one operation fails, appends `honua:OperationResults` in
`http://honua.io/wfs`.

Each submitted prepared operation appears exactly once, in request order, as
`honua:OperationResult`. Its `sequence`, `action`, optional request `handle`, and `committed`
attributes identify the disposition. Committed operations include an FES `ResourceId` when the
provider supplies one; failed operations include a sanitized `honua:Error`.
The `committed` attribute is tri-state: `true` confirms commit, `false` confirms rejection, and
`unknown` means the provider lost the commit acknowledgement and clients must not blindly retry.

This is a vendor extension, not a claim that the additional element belongs to the WFS 2.0 schema.
Clients that need complete partial-commit receipts must opt into understanding the Honua namespace.
The standard `rollbackOnFailure="true"` path remains atomic and returns an OWS exception after a
rollback instead of this extension.
