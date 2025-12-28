// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.ObjectPool;

namespace Honua.Benchmarks;

/// <summary>
/// Benchmarks for SQL query generation comparing different string building approaches.
/// Tests performance of StringBuilder vs ObjectPool of StringBuilder for various query scenarios.
///
/// Targets:
/// - Simple queries: less than 1μs allocation overhead
/// - Complex queries (joins, spatial): less than 10μs
/// - Memory allocations: less than 1KB per query for simple cases
/// </summary>
[MemoryDiagnoser]
public class SqlGenerationBenchmarks
{
    private ObjectPool<StringBuilder> _stringBuilderPool = null!;
    private readonly string[] _fieldNames = ["objectid", "name", "description", "created_date", "category", "status"];
    private readonly string[] _whereConditions =
    [
        "name = @p0",
        "category IN (@p0, @p1, @p2)",
        "created_date > @p0 AND status = @p1",
        "ST_Intersects(geom, ST_MakeEnvelope(@p0, @p1, @p2, @p3, 4326))"
    ];

    [GlobalSetup]
    public void Setup()
    {
        var provider = new DefaultObjectPoolProvider();
        _stringBuilderPool = provider.CreateStringBuilderPool();
    }

    /// <summary>
    /// Simple SELECT query using standard StringBuilder
    /// </summary>
    [Benchmark(Baseline = true, Description = "Simple SELECT - StringBuilder")]
    public string SimpleSelectWithStringBuilder()
    {
        var sql = new StringBuilder(256);
        sql.Append("SELECT ");

        for (int i = 0; i < _fieldNames.Length; i++)
        {
            if (i > 0)
                sql.Append(", ");
            sql.Append(_fieldNames[i]);
        }

        sql.Append(" FROM features WHERE objectid = @p0");
        return sql.ToString();
    }

    /// <summary>
    /// Simple SELECT query using ObjectPool of StringBuilder
    /// </summary>
    [Benchmark(Description = "Simple SELECT - ObjectPool")]
    public string SimpleSelectWithObjectPool()
    {
        var sql = _stringBuilderPool.Get();
        try
        {
            sql.Append("SELECT ");

            for (int i = 0; i < _fieldNames.Length; i++)
            {
                if (i > 0)
                    sql.Append(", ");
                sql.Append(_fieldNames[i]);
            }

            sql.Append(" FROM features WHERE objectid = @p0");
            return sql.ToString();
        }
        finally
        {
            _stringBuilderPool.Return(sql);
        }
    }

    /// <summary>
    /// Simple SELECT query using string concatenation
    /// </summary>
    [Benchmark(Description = "Simple SELECT - String concat")]
    public string SimpleSelectWithStringConcat()
    {
        return "SELECT " + string.Join(", ", _fieldNames) + " FROM features WHERE objectid = @p0";
    }

    /// <summary>
    /// Simple SELECT query using string interpolation
    /// </summary>
    [Benchmark(Description = "Simple SELECT - Interpolation")]
    public string SimpleSelectWithInterpolation()
    {
        var fields = string.Join(", ", _fieldNames);
        return $"SELECT {fields} FROM features WHERE objectid = @p0";
    }

    /// <summary>
    /// Complex spatial query with joins using StringBuilder
    /// </summary>
    [Benchmark(Description = "Complex spatial - StringBuilder")]
    public string ComplexSpatialQueryWithStringBuilder()
    {
        var sql = new StringBuilder(1024);
        sql.Append("SELECT f.objectid, f.name, f.geom, l.name as layer_name ");
        sql.Append("FROM features f ");
        sql.Append("INNER JOIN layers l ON f.layer_id = l.id ");
        sql.Append("WHERE ST_Intersects(f.geom, ST_MakeEnvelope(@p0, @p1, @p2, @p3, @p4)) ");
        sql.Append("AND f.status = @p5 ");
        sql.Append("AND l.visible = true ");
        sql.Append("ORDER BY f.objectid ");
        sql.Append("LIMIT @p6 OFFSET @p7");

        return sql.ToString();
    }

    /// <summary>
    /// Complex spatial query with joins using ObjectPool
    /// </summary>
    [Benchmark(Description = "Complex spatial - ObjectPool")]
    public string ComplexSpatialQueryWithObjectPool()
    {
        var sql = _stringBuilderPool.Get();
        try
        {
            sql.Append("SELECT f.objectid, f.name, f.geom, l.name as layer_name ");
            sql.Append("FROM features f ");
            sql.Append("INNER JOIN layers l ON f.layer_id = l.id ");
            sql.Append("WHERE ST_Intersects(f.geom, ST_MakeEnvelope(@p0, @p1, @p2, @p3, @p4)) ");
            sql.Append("AND f.status = @p5 ");
            sql.Append("AND l.visible = true ");
            sql.Append("ORDER BY f.objectid ");
            sql.Append("LIMIT @p6 OFFSET @p7");

            return sql.ToString();
        }
        finally
        {
            _stringBuilderPool.Return(sql);
        }
    }

    /// <summary>
    /// Dynamic WHERE clause building - performance critical for filter translation
    /// </summary>
    [Benchmark(Description = "Dynamic WHERE - StringBuilder")]
    public string DynamicWhereClauseWithStringBuilder()
    {
        var sql = new StringBuilder(512);
        sql.Append("SELECT * FROM features WHERE ");

        for (int i = 0; i < _whereConditions.Length; i++)
        {
            if (i > 0)
                sql.Append(" AND ");
            sql.Append('(').Append(_whereConditions[i]).Append(')');
        }

        return sql.ToString();
    }

    /// <summary>
    /// Dynamic WHERE clause building using ObjectPool
    /// </summary>
    [Benchmark(Description = "Dynamic WHERE - ObjectPool")]
    public string DynamicWhereClauseWithObjectPool()
    {
        var sql = _stringBuilderPool.Get();
        try
        {
            sql.Append("SELECT * FROM features WHERE ");

            for (int i = 0; i < _whereConditions.Length; i++)
            {
                if (i > 0)
                    sql.Append(" AND ");
                sql.Append('(').Append(_whereConditions[i]).Append(')');
            }

            return sql.ToString();
        }
        finally
        {
            _stringBuilderPool.Return(sql);
        }
    }

    /// <summary>
    /// Measure StringBuilder capacity optimization impact
    /// </summary>
    [Benchmark(Description = "Optimized capacity - StringBuilder")]
    public string OptimizedCapacityStringBuilder()
    {
        // Pre-calculated optimal capacity to reduce allocations
        var sql = new StringBuilder(450); // Measured optimal size for this query
        sql.Append("SELECT f.objectid, f.name, f.description, ST_AsGeoJSON(f.geom) as geometry ");
        sql.Append("FROM features f ");
        sql.Append("WHERE f.layer_id = @p0 ");
        sql.Append("AND ST_DWithin(f.geom, ST_GeogFromText(@p1), @p2) ");
        sql.Append("ORDER BY ST_Distance(f.geom, ST_GeogFromText(@p1)) ");
        sql.Append("LIMIT 1000");

        return sql.ToString();
    }

    /// <summary>
    /// Measure impact of using spans for string building
    /// </summary>
    [Benchmark(Description = "Span-based building")]
    public string SpanBasedStringBuilding()
    {
        Span<char> buffer = stackalloc char[512];
        int pos = 0;

        const string selectPart = "SELECT objectid, name FROM features WHERE ";
        selectPart.AsSpan().CopyTo(buffer[pos..]);
        pos += selectPart.Length;

        const string wherePart = "objectid = @p0";
        wherePart.AsSpan().CopyTo(buffer[pos..]);
        pos += wherePart.Length;

        return new string(buffer[..pos]);
    }

    /// <summary>
    /// Benchmark ArrayPool of char for large query building
    /// </summary>
    [Benchmark(Description = "ArrayPool char buffer")]
    public string ArrayPoolCharBuffer()
    {
        var pool = ArrayPool<char>.Shared;
        var buffer = pool.Rent(1024);

        try
        {
            int pos = 0;
            const string query = "SELECT f.*, ST_AsGeoJSON(f.geom) FROM features f WHERE f.layer_id = @p0 ORDER BY f.objectid LIMIT 1000";
            query.AsSpan().CopyTo(buffer.AsSpan(pos));
            pos += query.Length;

            return new string(buffer, 0, pos);
        }
        finally
        {
            pool.Return(buffer);
        }
    }

    /// <summary>
    /// Benchmark high-frequency parameter substitution scenario
    /// </summary>
    [Params(10, 50, 100)]
    public int ParameterCount { get; set; }

    [Benchmark(Description = "Many parameters - StringBuilder")]
    public string ManyParametersStringBuilder()
    {
        var sql = new StringBuilder(1024);
        sql.Append("SELECT * FROM features WHERE objectid IN (");

        for (int i = 0; i < ParameterCount; i++)
        {
            if (i > 0)
                sql.Append(", ");
            sql.Append("@p").Append(i);
        }

        sql.Append(')');
        return sql.ToString();
    }

    [Benchmark(Description = "Many parameters - ObjectPool")]
    public string ManyParametersObjectPool()
    {
        var sql = _stringBuilderPool.Get();
        try
        {
            sql.Append("SELECT * FROM features WHERE objectid IN (");

            for (int i = 0; i < ParameterCount; i++)
            {
                if (i > 0)
                    sql.Append(", ");
                sql.Append("@p").Append(i);
            }

            sql.Append(')');
            return sql.ToString();
        }
        finally
        {
            _stringBuilderPool.Return(sql);
        }
    }
}
