# Proto Contracts

This directory contains shared protobuf contracts used by Honua Server runtime components.

## Ownership After Repo Split

- JS SDK and MCP protobuf code generation is owned by `honua-sdk-js`.
- SDK-specific `buf generate` templates should live in the owning SDK repository, not in `honua-server`.

Keep `proto/` in this repository focused on contract definitions and compatibility checks.
