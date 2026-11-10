---
type: guide
title: "How to organize services in folders"
description: "Create a folder, move a service, and verify its public paths without changing access permissions."
---
# How to organize services in folders

Use folders to group services by subject, such as `Transport` or `Planning`.
The hierarchy is **folder → service → layer**, with one level of folders.
Services without a folder remain at **Root**.

You need a connected Console environment and permission to administer its services.
Open **Operate → Services**. If the Folders section reports missing, forbidden,
or unavailable, resolve that connection or permission problem before continuing.

## Create a folder

1. In **Folders**, select **New Folder**.
2. Enter a **Folder path name**, such as `Transport`. Use at most 64 ASCII
   letters, digits, underscores or hyphens, beginning with a letter. Names such
   as `Utilities` and protocol names such as `FeatureServer` are reserved.
3. Optionally enter a **Folder title**, such as `Transport services`. This is
   display text; it does not change the public path.
4. Choose **Create Folder** and wait for a completed result. An approval-required
   response is still pending; follow its separate approval channel.

## Move a service

1. Open the service's settings from **Operate → Services**.
2. In **Service Folder**, choose the **Destination Folder**, or **Root** to
   remove the service from a folder.
3. Choose **Move Service**. Wait for completion, then refresh the service settings.

The move keeps the service identity and its layers. A folder changes the public
path grouping; access remains controlled per service. Moving a service into a
folder does not grant its neighbors' permissions or make private data public.
See [access control](../secure/access-control.md).

## Rename or remove a folder

Select the existing folder in **Operate → Services → Folders**. Change the path
name or title and choose **Save Folder**. To clear a title, leave it blank.

To delete a folder, move all its services elsewhere first. Select the folder,
check **I want to delete this empty Folder**, and choose **Delete empty Folder**.
Deletion of a nonempty folder is refused.

## Verify the result

Reopen the service settings and inspect **Service endpoints**. For example, a
GeoServices path can change from `/rest/services/roads/FeatureServer` to
`/rest/services/Transport/roads/FeatureServer`. Use the endpoint advertised by
the server rather than constructing every protocol's URL from this example.

Existing public paths remain aliases after a move or folder path rename, so
saved client connections continue to resolve to the same service. An alias
does not bypass the service's access checks. Test both the advertised path and
an existing saved connection with the intended viewer account.

## Troubleshooting

- **The catalog changed or an ETag is missing:** choose **Refresh Folders**,
  review the current destination, and retry only after reading the new state.
- **The path is reserved or already used:** choose a different path name.
  Historical path reservations can prevent reusing an old name.
- **Approval is required:** retain the original operation or proposal reference
  and use [human approval](../secure/approve-agent-changes.md). Repeated moves
  do not substitute for a decision.
- **The folder moved but another view looks old:** refresh the view and inspect
  any reconciliation warning on the service settings before submitting again.

Next: [publish layers](publish-layers.md) or [connect a GIS client](../connect/qgis.md).
