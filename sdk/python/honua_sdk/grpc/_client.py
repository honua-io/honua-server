"""gRPC clients for Honua FeatureService."""
from __future__ import annotations

from collections.abc import Iterator
from typing import Any

import grpc

from honua_sdk.errors import HonuaError
from . import _models as models
from . import _proto_adapter as adapter


class HonuaGrpcError(HonuaError):
    """Raised when a gRPC call fails."""

    def __init__(self, code: grpc.StatusCode, message: str, details: Any = None) -> None:
        super().__init__(f"gRPC {code.name}: {message}")
        self.code = code
        self.message = message
        self.details = details


class HonuaGrpcClient:
    """Synchronous gRPC client for Honua FeatureService."""

    def __init__(
        self,
        target: str,
        *,
        channel: grpc.Channel | None = None,
        credentials: grpc.ChannelCredentials | None = None,
        metadata: list[tuple[str, str]] | None = None,
        compression: grpc.Compression | None = grpc.Compression.Gzip,
    ) -> None:
        self._metadata = metadata or []
        self._owns_channel = channel is None

        if channel is not None:
            self._channel = channel
        elif credentials is not None:
            self._channel = grpc.secure_channel(target, credentials, compression=compression)
        else:
            self._channel = grpc.insecure_channel(target, compression=compression)

        from honua_sdk.grpc._generated.honua.v1 import feature_service_pb2_grpc

        self._stub = feature_service_pb2_grpc.FeatureServiceStub(self._channel)

    def __enter__(self) -> HonuaGrpcClient:
        return self

    def __exit__(self, exc_type: object, exc: object, tb: object) -> None:
        self.close()

    def close(self) -> None:
        """Close the underlying channel if owned by this client."""
        if self._owns_channel:
            self._channel.close()

    def query_features(
        self, request: models.QueryFeaturesRequest
    ) -> models.QueryFeaturesResponse:
        """Execute a unary feature query and return the full response."""
        proto_request = adapter.to_proto_request(request)
        try:
            proto_response = self._stub.QueryFeatures(proto_request, metadata=self._metadata)
        except grpc.RpcError as e:
            raise HonuaGrpcError(e.code(), e.details() or str(e)) from e
        return adapter.from_proto_response(proto_response)

    def query_features_stream(
        self, request: models.QueryFeaturesRequest
    ) -> Iterator[models.FeaturePage]:
        """Execute a streaming feature query, yielding one page at a time."""
        proto_request = adapter.to_proto_request(request)
        try:
            stream = self._stub.QueryFeaturesStream(proto_request, metadata=self._metadata)
            for page in stream:
                yield adapter.from_proto_page(page)
                if page.is_last_page:
                    break
        except grpc.RpcError as e:
            raise HonuaGrpcError(e.code(), e.details() or str(e)) from e


class HonuaGrpcAsyncClient:
    """Async gRPC client for Honua FeatureService."""

    def __init__(
        self,
        target: str,
        *,
        channel: grpc.aio.Channel | None = None,
        credentials: grpc.ChannelCredentials | None = None,
        metadata: list[tuple[str, str]] | None = None,
        compression: grpc.Compression | None = grpc.Compression.Gzip,
    ) -> None:
        self._metadata = metadata or []
        self._owns_channel = channel is None

        if channel is not None:
            self._channel = channel
        elif credentials is not None:
            self._channel = grpc.aio.secure_channel(target, credentials, compression=compression)
        else:
            self._channel = grpc.aio.insecure_channel(target, compression=compression)

        from honua_sdk.grpc._generated.honua.v1 import feature_service_pb2_grpc

        self._stub = feature_service_pb2_grpc.FeatureServiceStub(self._channel)

    async def __aenter__(self) -> HonuaGrpcAsyncClient:
        return self

    async def __aexit__(self, exc_type: object, exc: object, tb: object) -> None:
        await self.close()

    async def close(self) -> None:
        """Close the underlying channel if owned by this client."""
        if self._owns_channel:
            await self._channel.close()

    async def query_features(
        self, request: models.QueryFeaturesRequest
    ) -> models.QueryFeaturesResponse:
        """Execute a unary feature query and return the full response."""
        proto_request = adapter.to_proto_request(request)
        try:
            proto_response = await self._stub.QueryFeatures(
                proto_request, metadata=self._metadata
            )
        except grpc.aio.AioRpcError as e:
            raise HonuaGrpcError(e.code(), e.details() or str(e)) from e
        return adapter.from_proto_response(proto_response)

    async def query_features_stream(
        self, request: models.QueryFeaturesRequest
    ):  # -> AsyncIterator[models.FeaturePage]
        """Execute a streaming feature query, yielding one page at a time."""
        proto_request = adapter.to_proto_request(request)
        try:
            stream = self._stub.QueryFeaturesStream(proto_request, metadata=self._metadata)
            async for page in stream:
                yield adapter.from_proto_page(page)
                if page.is_last_page:
                    break
        except grpc.aio.AioRpcError as e:
            raise HonuaGrpcError(e.code(), e.details() or str(e)) from e
