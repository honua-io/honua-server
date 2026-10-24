// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Marker interface signalling that the implementing feature store can return native
/// FlatGeobuf payloads from <see cref="IFeatureReader.QueryFlatGeobufAsync"/>. Protocol
/// adapters use this to gate the FlatGeobuf output path; readers that omit the marker
/// return <c>null</c> from <c>QueryFlatGeobufAsync</c> and would otherwise produce an
/// empty success response with the FlatGeobuf media type.
/// </summary>
public interface IFlatGeobufFeatureStore
{
}
