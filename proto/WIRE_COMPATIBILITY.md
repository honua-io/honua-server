# Protobuf Wire Compatibility Rules

This document defines the wire compatibility rules for protobuf contracts in this repository.
Proto definitions live at `src/Honua.Core/Transport/Proto/geospatial/v1/`.

## Rules for Proto Editors

### Field numbers are permanent

- Never change an existing field number.
- Never reuse a field number that was previously assigned, even if the field has been removed.

### Additive evolution only

- New fields must be **optional** and use a new (higher) field number.
- New `oneof` members may be added to an existing `oneof`, but do not remove existing members.

### Enum values

- Append new values at the end.
- Never reorder or renumber existing values.
- Never remove an enum value that has been released; mark it `[deprecated = true]` instead.

### Deprecation and removal

- Mark deprecated fields with `[deprecated = true]`.
- If a field is removed, add its number to a `reserved` declaration in the message:
  ```protobuf
  message Example {
    reserved 3, 8;
    reserved "old_field_name";
  }
  ```

### Breaking wire changes

A breaking wire change is any modification that causes existing serialized data to be misinterpreted by a consumer compiled against the previous schema. Examples:
- Changing a field's type (e.g., `int32` to `string`).
- Renumbering a field.
- Removing a field without reserving its number.
- Changing a field from `optional` to `repeated` (or vice versa).

Breaking wire changes require:
1. Explicit reviewer approval.
2. Documentation in `docs/developer/CONTROL_PLANE_MIGRATION_GUIDE.md`.
3. A documented migration and rollout plan for affected consumers.
4. Setting `BUF_ALLOW_BREAKING_CHANGES=true` in CI to bypass the `proto-wire-governance.yml` gate.

## CI Enforcement

Wire compatibility is enforced by `buf breaking` in the `proto-wire-governance.yml` workflow. This runs on every PR that modifies `.proto` files and compares against the PR base branch. Pushes compare against the previous commit on the protected branch.
