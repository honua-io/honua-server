// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using System.IO;
using Honua.DuckDB.Features.FeatureStore.Services;

namespace Honua.DuckDB.Tests;

public sealed class DuckDBFeatureDataAccessExceptionTests
{
    [Theory]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(DbException))]
    public void ShouldWrapQueryException_ReturnsTrueForSupportedQueryFailures(Type exceptionType)
    {
        var exception = exceptionType == typeof(DbException)
            ? new TestDbException("provider failure")
            : (Exception)Activator.CreateInstance(exceptionType, "provider failure")!;

        Assert.True(DuckDBFeatureDataAccess.ShouldWrapQueryException(exception));
    }

    [Fact]
    public void CreateSafeQueryException_ForTimeout_ReturnsSanitizedTimeout()
    {
        var inner = new TimeoutException("secret timeout details");

        var translated = DuckDBFeatureDataAccess.CreateSafeQueryException(7, "select", inner);

        Assert.IsType<TimeoutException>(translated);
        Assert.Equal("DuckDB select query timed out for layer 7.", translated.Message);
        Assert.DoesNotContain("secret timeout details", translated.Message, StringComparison.Ordinal);
        Assert.Same(inner, translated.InnerException);
    }

    [Fact]
    public void CreateSafeQueryException_ForGeneralFailure_ReturnsSanitizedInvalidOperation()
    {
        var inner = new InvalidOperationException("secret path /tmp/honua.duckdb");

        var translated = DuckDBFeatureDataAccess.CreateSafeQueryException(9, "statistics", inner);

        Assert.IsType<InvalidOperationException>(translated);
        Assert.Equal("DuckDB statistics query failed for layer 9.", translated.Message);
        Assert.DoesNotContain("/tmp/honua.duckdb", translated.Message, StringComparison.Ordinal);
        Assert.Same(inner, translated.InnerException);
    }

    private sealed class TestDbException : DbException
    {
        public TestDbException(string message)
            : base(message)
        {
        }
    }
}
