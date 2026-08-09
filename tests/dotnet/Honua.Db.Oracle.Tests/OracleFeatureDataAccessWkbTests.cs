// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Oracle.Features.FeatureStore.Services;

namespace Honua.Oracle.Tests;

/// <summary>
/// Unit tests for <see cref="OracleFeatureDataAccess.DecodeWkbValue"/>.
/// BH2-D03 regression: the previous catch-all arm silently returned null for unexpected
/// types (DBNull included), masking driver upgrade regressions and server-side conversion
/// errors by dropping geometry for the row rather than throwing a diagnostic exception.
/// </summary>
public sealed class OracleFeatureDataAccessWkbTests
{
    [Fact]
    public void DecodeWkbValue_WithByteArray_ReturnsSameInstance()
    {
        var bytes = new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00 };

        var result = OracleFeatureDataAccess.DecodeWkbValue(bytes, ordinal: 0);

        Assert.Same(bytes, result);
    }

    [Fact]
    public void DecodeWkbValue_WithDbNull_ReturnsNull()
    {
        // DBNull.Value is returned by OracleDataReader.GetValue when the column is NULL
        // (e.g. a row whose geometry was not populated or whose TO_WKBGEOMETRY result is null).
        var result = OracleFeatureDataAccess.DecodeWkbValue(DBNull.Value, ordinal: 5);

        Assert.Null(result);
    }

    [Fact]
    public void DecodeWkbValue_WithUnexpectedType_ThrowsInvalidOperationException()
    {
        // An integer value would indicate a driver bug or an unexpected column type —
        // should throw rather than silently returning null and losing the geometry.
        var ex = Assert.Throws<InvalidOperationException>(
            () => OracleFeatureDataAccess.DecodeWkbValue(42, ordinal: 3));

        Assert.Contains("ordinal 3", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Int32", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodeWkbValue_WithStringType_ThrowsInvalidOperationException()
    {
        // A string value would indicate a misconfigured SDO_UTIL.TO_WKBGEOMETRY or
        // a schema mismatch — throw so the failure is visible.
        var ex = Assert.Throws<InvalidOperationException>(
            () => OracleFeatureDataAccess.DecodeWkbValue("unexpected", ordinal: 1));

        Assert.Contains("String", ex.Message, StringComparison.Ordinal);
    }
}
