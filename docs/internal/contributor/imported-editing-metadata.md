# Imported editing metadata

GeoServices migration preserves the source GlobalID binding independently of
the target's object IDs. It uses the advertised `globalIdField`, or a field with
the Esri GlobalID type when that property is absent, and applies the same column
name normalization as feature import. Publication requires the binding to name
a published UUID column. The admin retained-table publication request accepts
`globalIdField` too; an absent or non-UUID column produces a validation failure.

Attachment support is projected into `resource.Editing.SupportsAttachments` when
the source advertises attachments, copying is requested and an attachment store
is registered. Attachment inventory, copied/failed counts and content fidelity
remain independent import evidence. A capability flag never proves all source
attachments arrived. The retained-table publication request can explicitly set
`supportsAttachments` after reviewing the configured attachment store and data.

FeatureServer and MapServer layer metadata read this canonical attachment flag.
Legacy annotations are consulted only for resources without an Editing object.
FeatureServer advertises a visible UUID GlobalID binding and the corresponding
`esriFieldTypeGlobalID` field; that identifier is not an ordinary editable field.
GlobalID-based applyEdits support is not claimed by these metadata bindings.

Import and retained-table publication remain read-only by default. They do not
copy source edit permissions. Before testing an Editor app, the target operator
must explicitly configure its publication edit capabilities and resource editing
intent through the target metadata workflow, retaining target authorization.
Local CRUD, persistence, cancellation and attachment add/read/delete must then be
qualified against a disposable service. This metadata fix does not itself
complete that enablement or the Editor application's interaction validation.

Regression coverage includes real PostGIS import/publication with normalized UUID
identity and attachment-copy opt-out/store-unavailable cases; public admin and
FeatureServer metadata readback; canonical-versus-legacy attachment precedence;
and visible versus hidden GlobalID fields. Track execution separately from the
presence of these tests in source. Owning workstream: server #4825.
