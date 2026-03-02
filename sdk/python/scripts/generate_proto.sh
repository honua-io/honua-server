#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SDK_DIR="$(dirname "$SCRIPT_DIR")"
PROTO_ROOT="$(dirname "$(dirname "$SDK_DIR")")/proto"
OUT_DIR="$SDK_DIR/honua_sdk/grpc/_generated"

mkdir -p "$OUT_DIR/honua/v1"

python3 -m grpc_tools.protoc \
  --proto_path="$PROTO_ROOT" \
  --python_out="$OUT_DIR" \
  --grpc_python_out="$OUT_DIR" \
  --pyi_out="$OUT_DIR" \
  honua/v1/feature_service.proto

# Fix imports in generated files
sed -i 's/from honua.v1 import/from honua_sdk.grpc._generated.honua.v1 import/g' \
  "$OUT_DIR/honua/v1/feature_service_pb2_grpc.py"
