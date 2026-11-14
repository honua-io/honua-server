---
type: guide
title: "Connect third-party field clients"
description: "Validate feature access, editing, conflict handling, and offline handoff with a standards-capable client."
---
# Connect third-party field clients

Use a client that supports OGC API Features for discovery and reading. Editing requires a client that also supports the advertised transaction operations, or WFS transactions. Read access alone does not establish edit or offline-sync support. Validate the exact client version against your deployment before collecting production data.

Honua first-party clients use gRPC and cloud-native files as described in [ADR-0087](../../internal/contributor/adr/0087-native-client-protocols.md). This guide covers third-party compatibility through OGC API Features and WFS.

## Connect and verify capabilities

In the client's connection dialog, set the OGC API Features service URL to your server's `/ogc/features` root, configure the deployment's authentication, and discover collections. Select a published collection that your account can read and edit. Inspect the service conformance and collection metadata before enabling optional operations or coordinate systems. For a WFS connection, use `/wfs` and inspect its capabilities, feature types, and transaction support.

On a small test layer, compare the client's feature count with the expected dataset. Follow returned page links until exhausted; a first page is not a complete offline baseline. Repeat with a page size smaller than the dataset and check for missing or duplicate IDs. Paging through a changing layer does not by itself provide a consistent snapshot; take a controlled export or reconcile concurrent changes.

Verify a known point in the client and in server results. Preserve the advertised coordinate reference system, coordinate order, and geometry dimensionality. Test the actual CRS negotiation used by the client, including the returned `Content-Crs`; do not relabel coordinates without transforming them. Confirm null geometries and required attributes behave as expected.

## Edit and resolve concurrent changes

Use a disposable layer and separate test identities with the intended permissions. Create a feature, read it back, change an attribute and geometry, and delete it. Check server-assigned IDs and persisted values after each operation. A successful connection does not prove that a client implements every write operation.

For OGC API Features, retain each item's `ETag` when reading and send it as `If-Match` on subsequent writes. Simulate two clients reading the same feature, then save from the first. The second client's stale PUT, PATCH, or DELETE must receive `412 Precondition Failed`; the first edit must survive. Fetch the current item, show both versions, and ask the operator to reconcile before resubmitting with the current version. Clients that cannot retain and submit versions need a controlled reconciliation workflow before editing shared data.

WFS transaction responses have their own success and failure structure. Do not assume OGC API Features ETags protect WFS edits. Verify the client's supported locking or conditional-update strategy separately, and inspect transaction counts, returned IDs, and exceptions. See [WFS transaction receipts](../wfs-transaction-receipts.md). After a timeout or an unknown commit outcome, read back the affected records before retrying a create or transaction.

## Verify an edit from an empty project

1. Open an empty project in the desktop client. Create the service connection described above using a test identity, discover collections, and add the disposable layer. Save the project without embedding credentials.
2. Identify a known feature and record its server ID, geometry, attributes, and base version. Start an edit session, change one attribute to a unique test value, and save through the service connection. Check the client's transaction result before continuing.
3. Open Honua Console against the same deployment with an authorized identity. Open the same published layer and feature ID, refresh its data, and confirm the unique value. Independently refresh the desktop layer to verify the persisted result rather than its local edit buffer.
4. Repeat with a new feature and a deletion, recording returned IDs and confirming both results in Console. Restore or remove the disposable test data through the API-backed client when finished.

Online writes require connectivity. An offline project or GeoPackage needs the explicit return workflow below before its edits become server data.

## Take a GeoPackage offline

A local GeoPackage is an editable copy, not an automatic synchronization contract. Before disconnecting:

1. Capture the selected layer, schema, CRS, permissions, and complete feature selection. Preserve server IDs separately from local row IDs and keep base versions or an unchanged baseline for conflict detection.
2. Record local creates, updates, and deletes explicitly. Retain deletion markers and attachment relationships; absence from a partial selection must never imply a server delete.
3. Reconnect through an authorized import or client reconciliation workflow. Compare local edits with current server state, resolve conflicts, validate attributes and geometry, and apply only the intended changes. Never blindly append an edited copy or replace a shared layer.
4. Verify persisted results and attachment references, then refresh the local baseline. Keep the original package and reconciliation report until verification completes.

A client must supply this workflow or be paired with a separately validated reconciliation tool. The server does not infer an offline edit journal or original ETags from an arbitrary GeoPackage. Direct database writes bypass the API's authorization and mutation processing and are outside this workflow.

## Observe changes

Where enabled and licensed, Honua change subscriptions provide normalized mutation notifications across supported write protocols. These are an additional Honua capability, not an OGC API Features or WFS client requirement. Use the published SDK subscription guidance in [React to feature changes](../edit/react-to-changes.md), deduplicate by event ID, and handle replay gaps by obtaining a fresh baseline. Do not treat a live notification stream alone as durable offline synchronization or assume that a disconnected client has received every change.

## Acceptance journey and evidence

Run this script with the exact client build and server configuration intended for deployment. Record versions, authentication scope, collection, CRS, feature IDs, observed results, and screenshots or logs with credentials removed.

| Step | Pass condition |
|---|---|
| Discover and page a layer | Expected IDs appear once across all pages; restricted layers remain inaccessible. |
| Read known geometry | Position, CRS, axis order, and attributes match the fixture. |
| Create, update, delete | Changes persist on a fresh read; IDs and transaction results are captured. |
| Reconnect after a competing edit | Stale writes fail without losing the accepted edit; reconciliation preserves the operator's choice. |
| Edit a GeoPackage offline | Only journaled changes are reconciled; deletes and conflicts are explicit; retry creates no duplicate. |
| Observe an enabled subscription | Accepted mutations are visible; reconnect or replay gaps trigger recovery rather than silent data loss. |

Server regression coverage includes `OgcFeaturesTransactionTests` (CRUD and conditional writes), `OgcFeaturesFieldClientConcurrencyTests` (stale PUT/PATCH/DELETE preserve the accepted version), `OgcFeaturesEnhancementsTests` (paging), `OgcFeaturesSpatialReferenceTests` (CRS), and `WfsFallbackGeometryTransactionTests` (WFS geometry transactions). These tests exercise server behavior; they do not certify an external client, its offline workflow, or phone installation. Run the acceptance journey on the target device before recording client interoperability as passed.
