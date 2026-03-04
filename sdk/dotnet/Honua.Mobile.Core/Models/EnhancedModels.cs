// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

namespace Honua.Mobile.Core.Models;

#region Geometry Enhancements

/// <summary>
/// Enhanced geometry with multiple encoding support.
/// </summary>
public class EnhancedGeometry
{
    public GeometryEncoding Encoding { get; set; } = GeometryEncoding.Structured;
    public StructuredGeometry? Structured { get; set; }
    public byte[]? Wkb { get; set; }
    public string? Wkt { get; set; }
    public string? GeoJson { get; set; }
    public byte[]? EsriShape { get; set; }
    public SpatialReference? SpatialReference { get; set; }
    public BoundingBox? Envelope { get; set; }
    public GeometryQuality? Quality { get; set; }

    /// <summary>
    /// Creates a geometry from structured proto format.
    /// </summary>
    public static EnhancedGeometry FromStructured(StructuredGeometry geometry)
    {
        return new EnhancedGeometry
        {
            Encoding = GeometryEncoding.Structured,
            Structured = geometry
        };
    }

    /// <summary>
    /// Creates a geometry from Well-Known Binary.
    /// </summary>
    public static EnhancedGeometry FromWkb(byte[] wkb, SpatialReference? spatialReference = null)
    {
        return new EnhancedGeometry
        {
            Encoding = GeometryEncoding.Wkb,
            Wkb = wkb,
            SpatialReference = spatialReference
        };
    }

    /// <summary>
    /// Creates a geometry from Well-Known Text.
    /// </summary>
    public static EnhancedGeometry FromWkt(string wkt, SpatialReference? spatialReference = null)
    {
        return new EnhancedGeometry
        {
            Encoding = GeometryEncoding.Wkt,
            Wkt = wkt,
            SpatialReference = spatialReference
        };
    }

    /// <summary>
    /// Creates a geometry from GeoJSON.
    /// </summary>
    public static EnhancedGeometry FromGeoJson(string geoJson)
    {
        return new EnhancedGeometry
        {
            Encoding = GeometryEncoding.GeoJson,
            GeoJson = geoJson
        };
    }
}

public class StructuredGeometry
{
    public PointGeometry? Point { get; set; }
    public MultiPointGeometry? MultiPoint { get; set; }
    public PolylineGeometry? Polyline { get; set; }
    public PolygonGeometry? Polygon { get; set; }
    public MultiPolygonGeometry? MultiPolygon { get; set; }

    public GeometryType GetGeometryType()
    {
        if (Point != null) return GeometryType.Point;
        if (MultiPoint != null) return GeometryType.MultiPoint;
        if (Polyline != null) return GeometryType.LineString;
        if (Polygon != null) return GeometryType.Polygon;
        if (MultiPolygon != null) return GeometryType.MultiPolygon;
        return GeometryType.None;
    }
}

public enum GeometryEncoding
{
    Unspecified = 0,
    Structured = 1,
    Wkb = 2,
    Wkt = 3,
    GeoJson = 4,
    EsriShape = 5
}

public class BoundingBox
{
    public double Xmin { get; set; }
    public double Ymin { get; set; }
    public double Xmax { get; set; }
    public double Ymax { get; set; }

    public bool IsEmpty => Xmin >= Xmax || Ymin >= Ymax;

    public double Width => Xmax - Xmin;
    public double Height => Ymax - Ymin;

    public PointGeometry Center => PointGeometry.Create((Xmin + Xmax) / 2, (Ymin + Ymax) / 2);

    public static BoundingBox FromPoints(IEnumerable<PointGeometry> points)
    {
        var pointList = points.ToList();
        if (!pointList.Any())
            return new BoundingBox();

        return new BoundingBox
        {
            Xmin = pointList.Min(p => p.X),
            Xmax = pointList.Max(p => p.X),
            Ymin = pointList.Min(p => p.Y),
            Ymax = pointList.Max(p => p.Y)
        };
    }

    public bool Intersects(BoundingBox other)
    {
        return !(Xmax < other.Xmin || Xmin > other.Xmax ||
                Ymax < other.Ymin || Ymin > other.Ymax);
    }
}

public class GeometryQuality
{
    public double SimplificationTolerance { get; set; }
    public int CoordinatePrecision { get; set; }
    public bool TopologyPreserved { get; set; }
    public double? OriginalArea { get; set; }
}

#endregion

#region Enhanced Spatial Reference

/// <summary>
/// Enhanced spatial reference with rich metadata.
/// </summary>
public class EnhancedSpatialReference : SpatialReference
{
    public string? AuthorityCode { get; set; } // e.g., "EPSG:4326"
    public string? Proj4 { get; set; }
    public CoordinateSystemType Type { get; set; }
    public GeographicBounds? Bounds { get; set; }
    public double LinearUnitScale { get; set; } = 1.0;
    public double AngularUnitScale { get; set; } = 1.0;
    public string? DisplayName { get; set; }

    /// <summary>
    /// Common spatial reference systems.
    /// </summary>
    public static class Common
    {
        public static readonly EnhancedSpatialReference WGS84 = new()
        {
            Wkid = 4326,
            AuthorityCode = "EPSG:4326",
            DisplayName = "WGS 84",
            Type = CoordinateSystemType.Geographic,
            Bounds = new GeographicBounds
            {
                WestLongitude = -180,
                EastLongitude = 180,
                SouthLatitude = -90,
                NorthLatitude = 90
            },
            AngularUnitScale = Math.PI / 180 // Degrees to radians
        };

        public static readonly EnhancedSpatialReference WebMercator = new()
        {
            Wkid = 3857,
            LatestWkid = 3857,
            AuthorityCode = "EPSG:3857",
            DisplayName = "WGS 84 / Pseudo-Mercator",
            Type = CoordinateSystemType.Projected,
            LinearUnitScale = 1.0, // Meters
            Bounds = new GeographicBounds
            {
                WestLongitude = -180,
                EastLongitude = 180,
                SouthLatitude = -85.0511,
                NorthLatitude = 85.0511
            }
        };
    }

    /// <summary>
    /// Validates if coordinates are within the valid bounds for this spatial reference.
    /// </summary>
    public bool AreCoordinatesValid(double x, double y)
    {
        if (Bounds == null) return true;

        return Type switch
        {
            CoordinateSystemType.Geographic =>
                x >= Bounds.WestLongitude && x <= Bounds.EastLongitude &&
                y >= Bounds.SouthLatitude && y <= Bounds.NorthLatitude,

            CoordinateSystemType.Projected =>
                // For projected systems, bounds are typically in the projected units
                true, // TODO: Implement projected bounds validation

            _ => true
        };
    }
}

public enum CoordinateSystemType
{
    Unspecified = 0,
    Geographic = 1,
    Projected = 2,
    Geocentric = 3,
    Local = 4
}

public class GeographicBounds
{
    public double WestLongitude { get; set; }
    public double EastLongitude { get; set; }
    public double SouthLatitude { get; set; }
    public double NorthLatitude { get; set; }
}

#endregion

#region Query Filtering

/// <summary>
/// Enhanced query filtering with compound logic.
/// </summary>
public abstract class QueryFilter
{
    public static AttributeFilter Attribute(string expression) => new(expression);
    public static SpatialFilter Spatial(Geometry geometry, SpatialRelationship relationship) => new(geometry, relationship);
    public static TemporalFilter Temporal(string field, DateTime start, DateTime end) => new(field, start, end);
    public static CompoundFilter Compound(LogicalOperator op) => new(op);

    /// <summary>
    /// Creates a compound filter with AND logic.
    /// </summary>
    public static CompoundFilter And(params QueryFilter[] filters) => new CompoundFilter(LogicalOperator.And, filters);

    /// <summary>
    /// Creates a compound filter with OR logic.
    /// </summary>
    public static CompoundFilter Or(params QueryFilter[] filters) => new CompoundFilter(LogicalOperator.Or, filters);

    /// <summary>
    /// Creates a compound filter with NOT logic.
    /// </summary>
    public static CompoundFilter Not(QueryFilter filter) => new CompoundFilter(LogicalOperator.Not, filter);
}

public class AttributeFilter : QueryFilter
{
    public string Expression { get; set; }

    public AttributeFilter(string expression)
    {
        Expression = expression;
    }
}

public class TemporalFilter : QueryFilter
{
    public string TimeField { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public TemporalRelationship Relationship { get; set; }

    public TemporalFilter(string timeField, DateTime? startTime = null, DateTime? endTime = null)
    {
        TimeField = timeField;
        StartTime = startTime;
        EndTime = endTime;
        Relationship = TemporalRelationship.During;
    }

    public static TemporalFilter CreatedAfter(DateTime date) => new("CREATED_DATE", date, null) { Relationship = TemporalRelationship.After };
    public static TemporalFilter ModifiedSince(DateTime date) => new("MODIFIED_DATE", date, null) { Relationship = TemporalRelationship.After };
    public static TemporalFilter Between(string field, DateTime start, DateTime end) => new(field, start, end);
}

public enum TemporalRelationship
{
    Unspecified = 0,
    During = 1,
    Contains = 2,
    Overlaps = 3,
    Intersects = 4,
    Before = 5,
    After = 6
}

public class CompoundFilter : QueryFilter
{
    public LogicalOperator Operator { get; set; }
    public List<QueryFilter> Filters { get; set; } = new();

    public CompoundFilter(LogicalOperator op, params QueryFilter[] filters)
    {
        Operator = op;
        Filters.AddRange(filters);
    }

    public CompoundFilter And(QueryFilter filter)
    {
        if (Operator == LogicalOperator.And)
        {
            Filters.Add(filter);
            return this;
        }
        return new CompoundFilter(LogicalOperator.And, this, filter);
    }

    public CompoundFilter Or(QueryFilter filter)
    {
        if (Operator == LogicalOperator.Or)
        {
            Filters.Add(filter);
            return this;
        }
        return new CompoundFilter(LogicalOperator.Or, this, filter);
    }
}

public enum LogicalOperator
{
    Unspecified = 0,
    And = 1,
    Or = 2,
    Not = 3
}

#endregion

#region Mobile Optimizations

/// <summary>
/// Mobile optimization settings for queries and responses.
/// </summary>
public class MobileOptimizations
{
    public List<string> PriorityFields { get; set; } = new();
    public CachePolicy? CachePolicy { get; set; }
    public CompressionLevel Compression { get; set; } = CompressionLevel.Medium;
    public bool LowPowerMode { get; set; }

    public static MobileOptimizations Default => new();

    public static MobileOptimizations ForLowPower() => new()
    {
        LowPowerMode = true,
        Compression = CompressionLevel.Low,
        PriorityFields = { "OBJECTID", "NAME" }
    };

    public static MobileOptimizations ForHighPerformance() => new()
    {
        Compression = CompressionLevel.High,
        CachePolicy = CachePolicy.Aggressive(),
        PriorityFields = { "OBJECTID" }
    };
}

public class CachePolicy
{
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromHours(1);
    public bool AllowStaleWhileRevalidate { get; set; } = true;
    public List<string> CacheTags { get; set; } = new();

    public static CachePolicy Conservative() => new()
    {
        MaxAge = TimeSpan.FromMinutes(5),
        AllowStaleWhileRevalidate = false
    };

    public static CachePolicy Aggressive() => new()
    {
        MaxAge = TimeSpan.FromHours(24),
        AllowStaleWhileRevalidate = true
    };
}

public enum CompressionLevel
{
    Unspecified = 0,
    None = 1,
    Low = 2,
    Medium = 3,
    High = 4
}

/// <summary>
/// Level of detail configuration for geometry simplification.
/// </summary>
public class LevelOfDetail
{
    public double MinScale { get; set; }
    public double MaxScale { get; set; }
    public double Tolerance { get; set; }
    public GeometryType? SimplifiedType { get; set; }
    public bool PreserveTopology { get; set; } = true;

    /// <summary>
    /// Creates LOD settings for mobile map viewing.
    /// </summary>
    public static LevelOfDetail ForMobileMap(double zoomLevel) => new()
    {
        MinScale = zoomLevel * 0.5,
        MaxScale = zoomLevel * 2.0,
        Tolerance = Math.Max(1.0, 100.0 / zoomLevel), // Smaller tolerance for closer zoom
        PreserveTopology = zoomLevel > 10 // Only preserve topology for close zoom
    };

    /// <summary>
    /// Creates LOD settings for list/table display.
    /// </summary>
    public static LevelOfDetail ForListDisplay() => new()
    {
        SimplifiedType = GeometryType.Point, // Always use points for lists
        Tolerance = 100, // Very simplified
        PreserveTopology = false
    };
}

#endregion

#region Error Handling

/// <summary>
/// Structured error information with actionable details.
/// </summary>
public class StructuredError
{
    public ErrorCode Code { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<ErrorDetail> Details { get; set; } = new();
    public Dictionary<string, string> Metadata { get; set; } = new();
    public string? RequestId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public bool IsRetryable => Code switch
    {
        ErrorCode.ServiceUnavailable => true,
        ErrorCode.Timeout => true,
        ErrorCode.RateLimitExceeded => true,
        _ => false
    };

    public TimeSpan? RetryAfter => Metadata.TryGetValue("retry_after", out var value) &&
        TimeSpan.TryParse(value, out var delay) ? delay : null;
}

public enum ErrorCode
{
    Unspecified = 0,
    InvalidQuery = 1,
    GeometryError = 2,
    SpatialReferenceError = 3,
    AuthenticationError = 4,
    AuthorizationError = 5,
    RateLimitExceeded = 6,
    ServiceUnavailable = 7,
    Timeout = 8,
    InvalidParameters = 9,
    LayerNotFound = 10,
    FeatureNotFound = 11,
    EditConflict = 12
}

public class ErrorDetail
{
    public string FieldName { get; set; } = string.Empty;
    public string Violation { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? HelpUrl { get; set; }
}

#endregion

#region Sync Models

/// <summary>
/// Synchronization request configuration.
/// </summary>
public class SyncRequest
{
    public string ClientId { get; set; } = string.Empty;
    public long LastSyncGeneration { get; set; }
    public List<LayerSyncInfo> Layers { get; set; } = new();
    public SyncStrategy Strategy { get; set; } = SyncStrategy.Incremental;
    public BoundingBox? SyncExtent { get; set; }
}

public class LayerSyncInfo
{
    public string ServiceId { get; set; } = string.Empty;
    public int LayerId { get; set; }
    public long LastGeneration { get; set; }
    public BoundingBox? SyncExtent { get; set; }
}

public enum SyncStrategy
{
    Unspecified = 0,
    Full = 1,
    Incremental = 2,
    ConflictResolution = 3
}

/// <summary>
/// Synchronization result with conflict information.
/// </summary>
public class SyncResult
{
    public bool IsSuccess { get; set; }
    public long FinalGeneration { get; set; }
    public int ChangesApplied { get; set; }
    public int ConflictsResolved { get; set; }
    public List<FeatureConflict> UnresolvedConflicts { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public TimeSpan Duration { get; set; }
    public DateTime CompletionTime { get; set; }
    public StructuredError? Error { get; set; }
}

/// <summary>
/// Feature conflict information for manual resolution.
/// </summary>
public class FeatureConflict
{
    public long ObjectId { get; set; }
    public Feature? ServerVersion { get; set; }
    public Feature? ClientVersion { get; set; }
    public ConflictType ConflictType { get; set; }
    public List<string> ConflictingFields { get; set; } = new();
}

public enum ConflictType
{
    Unspecified = 0,
    UpdateUpdate = 1,
    UpdateDelete = 2,
    DeleteUpdate = 3,
    GeometryMismatch = 4
}

/// <summary>
/// Conflict resolution decision.
/// </summary>
public class ConflictResolution
{
    public long ObjectId { get; set; }
    public ResolutionStrategy Strategy { get; set; }
    public Feature? ResolvedFeature { get; set; }
}

public enum ResolutionStrategy
{
    Unspecified = 0,
    ClientWins = 1,
    ServerWins = 2,
    ManualMerge = 3,
    Skip = 4
}

public enum ConflictResolutionStrategy
{
    Unspecified = 0,
    Fail = 1,
    ServerWins = 2,
    ClientWins = 3,
    Manual = 4
}

#endregion

#region Metadata Models

/// <summary>
/// Service metadata for optimization and capabilities.
/// </summary>
public class ServiceMetadata
{
    public ServiceInfo? ServiceInfo { get; set; }
    public List<LayerInfo> Layers { get; set; } = new();
    public StructuredError? Error { get; set; }
}

public class ServiceInfo
{
    public string ServiceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public SpatialReference? DefaultSpatialReference { get; set; }
    public BoundingBox? FullExtent { get; set; }
    public List<string> SupportedFormats { get; set; } = new();
    public ServiceCapabilities? Capabilities { get; set; }
}

public class ServiceCapabilities
{
    public bool SupportsEditing { get; set; }
    public bool SupportsStreaming { get; set; }
    public bool SupportsSync { get; set; }
    public List<GeometryEncoding> SupportedEncodings { get; set; } = new();
    public int MaxRecordCount { get; set; } = 1000;
    public int MaxBatchSize { get; set; } = 100;
}

public class LayerInfo
{
    public int LayerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public GeometryType GeometryType { get; set; }
    public List<FieldDefinition> Fields { get; set; } = new();
    public LayerCapabilities? Capabilities { get; set; }
    public SpatialIndexInfo? SpatialIndex { get; set; }
}

public class LayerCapabilities
{
    public bool SupportsQuery { get; set; } = true;
    public bool SupportsEditing { get; set; }
    public bool SupportsAttachments { get; set; }
    public List<SpatialRelationship> SupportedRelationships { get; set; } = new();
    public int MaxFeatures { get; set; } = 100000;
}

public class SpatialIndexInfo
{
    public string IndexType { get; set; } = string.Empty;
    public double Tolerance { get; set; }
    public BoundingBox? IndexExtent { get; set; }
    public bool IsOptimizedForMobile { get; set; }
}

/// <summary>
/// Query execution metadata.
/// </summary>
public class QueryMetadata
{
    public DateTime ExecutionTime { get; set; }
    public TimeSpan ExecutionDuration { get; set; }
    public bool UsedSpatialIndex { get; set; }
    public string? QueryPlan { get; set; }
    public List<string> Warnings { get; set; } = new();
    public int GeometrySimplificationLevel { get; set; }
}

/// <summary>
/// Edit operation metadata.
/// </summary>
public class EditMetadata
{
    public string? ClientId { get; set; }
    public string? UserId { get; set; }
    public string? SessionId { get; set; }
    public Dictionary<string, string> CustomAttributes { get; set; } = new();
    public DateTime ClientTimestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Edit operation summary.
/// </summary>
public class EditSummary
{
    public int TotalEdits { get; set; }
    public int SuccessfulEdits { get; set; }
    public int FailedEdits { get; set; }
    public int ConflictsDetected { get; set; }
    public DateTime ServerTimestamp { get; set; }
    public long NewGeneration { get; set; }
}

/// <summary>
/// Feature metadata for audit trails and sync tracking.
/// </summary>
public class FeatureMetadata
{
    public DateTime? CreatedAt { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public string? CreatedBy { get; set; }
    public string? ModifiedBy { get; set; }
    public long Generation { get; set; }
    public Dictionary<string, string> CustomMetadata { get; set; } = new();
}

/// <summary>
/// Feature change for sync operations.
/// </summary>
public class FeatureChange
{
    public ChangeOperation Operation { get; set; }
    public Feature? Feature { get; set; }
    public long Generation { get; set; }
    public DateTime Timestamp { get; set; }
    public string ChangeId { get; set; } = string.Empty;
}

public enum ChangeOperation
{
    Unspecified = 0,
    Insert = 1,
    Update = 2,
    Delete = 3
}

/// <summary>
/// Edit batch for streaming operations.
/// </summary>
public class EditBatch
{
    public List<Feature> Adds { get; set; } = new();
    public List<Feature> Updates { get; set; } = new();
    public List<long> Deletes { get; set; } = new();
    public bool RollbackOnFailure { get; set; } = true;
    public int BatchId { get; set; }
    public bool IsFinalBatch { get; set; }
}

#endregion