// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Marks a feature reader whose provider applies <c>FeatureQuery.Distinct</c> before
/// pagination. FeatureServer uses this capability to retain a correct, materialized
/// fallback for providers that expose ordinary rows only.
/// </summary>
public interface IDistinctFeatureReader
{
}
