// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Wfs20.Models;
using Honua.Server.Features.Wfs20.Services;
using System.Reflection;

namespace Honua.Server.Tests.Features.Wfs20;

/// <summary>
/// Comprehensive tests for WFS 2.0 FilterCapabilities compliance validation.
/// Ensures that all implemented capabilities are properly advertised to achieve 95%+ OGC compliance.
/// </summary>
public class Wfs20FilterCapabilitiesComplianceTests
{
    private static readonly string[] RequiredSpatialFunctions = ["ST_Area", "ST_Length", "ST_Buffer", "ST_Centroid"];

    /// <summary>
    /// Validates that all 15 Allen interval temporal operators are correctly advertised.
    /// This addresses the critical gap where backend supports all operators but only some were being advertised.
    /// </summary>
    [Fact]
    public void FilterCapabilities_TemporalOperators_ShouldAdvertiseAllAllenIntervalOperators()
    {
        // Arrange
        var filterCapabilities = GetActualFilterCapabilities();
        var temporalOps = filterCapabilities.TemporalCapabilities!.TemporalOperators!.Operators;
        var operatorNames = temporalOps.Select(op => op.Name).ToHashSet();

        // Act & Assert - All 15 Allen interval operators per OGC Filter Encoding 2.0
        var allenIntervalOperators = new[]
        {
            "After",        // A after B
            "Before",       // A before B
            "During",       // A during B
            "Contains",     // A contains B
            "Equals",       // A equals B
            "Disjoint",     // A disjoint B
            "Intersects",   // A intersects B
            "Meets",        // A meets B
            "MetBy",        // A met by B
            "Overlaps",     // A overlaps B
            "OverlappedBy", // A overlapped by B
            "Starts",       // A starts B
            "StartedBy",    // A started by B
            "Finishes",     // A finishes B
            "FinishedBy"    // A finished by B
        };

        // Verify each Allen interval operator is advertised
        foreach (var expectedOperator in allenIntervalOperators)
        {
            operatorNames.Should().Contain(expectedOperator,
                $"Allen interval operator '{expectedOperator}' must be advertised for full temporal compliance");
        }

        // Should have exactly 15 temporal operators (complete Allen interval coverage)
        temporalOps.Should().HaveCount(15,
            "Should advertise exactly 15 Allen interval temporal operators for complete OGC compliance");

        // Verify operator naming follows OGC convention (no T- prefix)
        foreach (var op in temporalOps)
        {
            op.Name.Should().MatchRegex(@"^[A-Z][a-zA-Z]*$",
                $"Temporal operator '{op.Name}' should use proper OGC naming (CapitalCase, no T- prefix)");
            op.Name.Should().NotStartWith("T_",
                $"Temporal operator '{op.Name}' should not use legacy T_ prefix");
        }
    }

    /// <summary>
    /// Validates that comprehensive spatial functions are properly advertised.
    /// Ensures ST_Area, ST_Length, ST_Buffer, ST_Centroid and other spatial functions are discoverable.
    /// </summary>
    [Fact]
    public void FilterCapabilities_SpatialFunctions_ShouldAdvertiseComprehensiveSpatialCapabilities()
    {
        // Arrange
        var filterCapabilities = GetActualFilterCapabilities();
        var functions = filterCapabilities.Functions!.Functions;
        var functionNames = functions.Select(f => f.Name).ToHashSet();

        // Act & Assert - Core spatial functions that must be advertised
        var coreSpatialFunctions = new[]
        {
            "ST_Area",          // Calculate area of polygon
            "ST_Length",        // Calculate length of linestring
            "ST_Distance",      // Distance between geometries
            "ST_Buffer",        // Buffer around geometry
            "ST_Centroid",      // Centroid of geometry
            "ST_IsValid",       // Geometry validation
            "ST_GeometryType",  // Geometry type identification
            "ST_NumGeometries", // Count geometries in collection
            "ST_Envelope",      // Bounding box
            "ST_ConvexHull",    // Convex hull
            "ST_Boundary",      // Geometry boundary
            "ST_SRID",          // Spatial reference ID
            "ST_IsSimple",      // Simplicity test
            "ST_IsClosed",      // Closure test
            "ST_IsEmpty"        // Emptiness test
        };

        foreach (var expectedFunction in coreSpatialFunctions)
        {
            functionNames.Should().Contain(expectedFunction,
                $"Spatial function '{expectedFunction}' must be advertised for spatial capability discovery");
        }

        // Verify function definitions are complete
        foreach (var func in functions.Where(f => f.Name.StartsWith("ST_")))
        {
            func.Returns.Should().NotBeNull(
                $"Spatial function '{func.Name}' must specify return type");
            func.Returns!.Type.Should().NotBeNullOrWhiteSpace(
                $"Spatial function '{func.Name}' must have valid return type");
        }
    }

    /// <summary>
    /// Validates conformance constraints accurately reflect implementation capabilities.
    /// Fixes the issue where ImplementsExtendedOperators was incorrectly constrained.
    /// </summary>
    [Fact]
    public void FilterCapabilities_ConformanceConstraints_ShouldAccuratelyReflectImplementation()
    {
        // Arrange
        var filterCapabilities = GetActualFilterCapabilities();
        var constraints = filterCapabilities.Conformance.Constraints;
        var constraintDict = constraints.ToDictionary(c => c.Name, c => c.DefaultValue);

        // Act & Assert - Core OGC Filter Encoding 2.0 conformance classes (must be TRUE)
        var coreConformanceClasses = new[]
        {
            "ImplementsQuery",
            "ImplementsAdHocQuery",
            "ImplementsResourceId",
            "ImplementsStandardFilter",
            "ImplementsMinSpatialFilter",
            "ImplementsSpatialFilter",
            "ImplementsMinTemporalFilter",
            "ImplementsTemporalFilter",
            "ImplementsFunctions"
        };

        foreach (var conformanceClass in coreConformanceClasses)
        {
            constraintDict.Should().ContainKey(conformanceClass,
                $"Core conformance class '{conformanceClass}' must be declared");
            constraintDict[conformanceClass].Should().Be("TRUE",
                $"Core conformance class '{conformanceClass}' should be enabled");
        }

        // Enhanced capability constraints (must be TRUE given implementation)
        var enhancedCapabilities = new[]
        {
            "ImplementsExtendedOperators",     // Critical: was incorrectly FALSE
            "ImplementsArithmeticOperators",
            "ImplementsLogicalOperators",
            "ImplementsComparisonOperators"
        };

        foreach (var capability in enhancedCapabilities)
        {
            constraintDict.Should().ContainKey(capability,
                $"Enhanced capability '{capability}' must be declared");
            constraintDict[capability].Should().Be("TRUE",
                $"Enhanced capability '{capability}' should be enabled given implementation");
        }

        // CQL2 support constraints (must be TRUE for modern compliance)
        var cql2Capabilities = new[]
        {
            "ImplementsCQL2Text",
            "ImplementsCQL2JSON",
            "ImplementsCQL2BasicCQL",
            "ImplementsCQL2AdvancedComparison",
            "ImplementsCQL2BasicSpatial",
            "ImplementsCQL2SpatialOperators",
            "ImplementsCQL2TemporalOperators",
            "ImplementsCQL2Functions"
        };

        foreach (var cql2Capability in cql2Capabilities)
        {
            constraintDict.Should().ContainKey(cql2Capability,
                $"CQL2 capability '{cql2Capability}' must be declared");
            constraintDict[cql2Capability].Should().Be("TRUE",
                $"CQL2 capability '{cql2Capability}' should be enabled");
        }
    }

    /// <summary>
    /// Validates comprehensive function coverage across all categories.
    /// Ensures string, math, date/time, spatial, and aggregate functions are all advertised.
    /// </summary>
    [Fact]
    public void FilterCapabilities_Functions_ShouldProvideComprehensiveFunctionCoverage()
    {
        // Arrange
        var filterCapabilities = GetActualFilterCapabilities();
        var functions = filterCapabilities.Functions!.Functions;
        var functionNames = functions.Select(f => f.Name).ToHashSet();

        // Act & Assert - String functions
        var stringFunctions = new[] { "UPPER", "LOWER", "CONCAT", "SUBSTRING", "LENGTH", "TRIM", "REPLACE" };
        foreach (var func in stringFunctions)
        {
            functionNames.Should().Contain(func, $"String function '{func}' should be advertised");
        }

        // Math functions
        var mathFunctions = new[] { "ABS", "CEIL", "FLOOR", "ROUND", "SQRT", "SIN", "COS", "TAN", "LOG", "EXP", "POWER", "MOD" };
        foreach (var func in mathFunctions)
        {
            functionNames.Should().Contain(func, $"Math function '{func}' should be advertised");
        }

        // Date/time functions
        var dateFunctions = new[] { "YEAR", "MONTH", "DAY", "HOUR", "MINUTE", "SECOND", "NOW" };
        foreach (var func in dateFunctions)
        {
            functionNames.Should().Contain(func, $"Date/time function '{func}' should be advertised");
        }

        // Aggregate functions
        var aggregateFunctions = new[] { "COUNT", "SUM", "AVG", "MIN", "MAX" };
        foreach (var func in aggregateFunctions)
        {
            functionNames.Should().Contain(func, $"Aggregate function '{func}' should be advertised");
        }

        // Should have comprehensive coverage (35+ functions for full compliance)
        functions.Should().HaveCountGreaterOrEqualTo(35,
            "Should advertise at least 35 functions for comprehensive OGC compliance");
    }

    /// <summary>
    /// Validates spatial capabilities include full geometry and operator support.
    /// Ensures DE-9IM topology operators and comprehensive geometry types are advertised.
    /// </summary>
    [Fact]
    public void FilterCapabilities_SpatialCapabilities_ShouldSupportFullOGCGeometryModel()
    {
        // Arrange
        var filterCapabilities = GetActualFilterCapabilities();
        var spatialCaps = filterCapabilities.SpatialCapabilities!;

        // Act & Assert - Geometry operands (geometry types supported)
        var geometryOperands = spatialCaps.GeometryOperands!.Operands
            .Select(op => op.Name.Name).ToHashSet();

        var coreGeometryTypes = new[]
        {
            "Point", "LineString", "Polygon", "Envelope",
            "MultiPoint", "MultiLineString", "MultiPolygon",
            "MultiGeometry", "GeometryCollection"
        };

        foreach (var geomType in coreGeometryTypes)
        {
            geometryOperands.Should().Contain(geomType,
                $"Geometry type '{geomType}' should be supported for full OGC geometry model compliance");
        }

        // Spatial operators (should include all DE-9IM topology operators)
        var spatialOperators = spatialCaps.SpatialOperators!.Operators
            .Select(op => op.Name).ToHashSet();

        var coreTopologyOperators = new[]
        {
            "BBOX",       // Bounding box intersection
            "Intersects", // Geometries intersect
            "Contains",   // A contains B
            "Within",     // A within B
            "Crosses",    // A crosses B
            "Touches",    // A touches B
            "Overlaps",   // A overlaps B
            "Disjoint",   // A disjoint from B
            "Equals",     // A equals B
            "DWithin",    // A within distance of B
            "Beyond"      // A beyond distance from B
        };

        foreach (var spatialOp in coreTopologyOperators)
        {
            spatialOperators.Should().Contain(spatialOp,
                $"Spatial operator '{spatialOp}' should be supported for complete DE-9IM compliance");
        }

        // Should have comprehensive spatial operator coverage (11+ for full DE-9IM)
        spatialCaps.SpatialOperators!.Operators.Should().HaveCountGreaterOrEqualTo(11,
            "Should support at least 11 spatial operators for complete DE-9IM topology compliance");
    }

    /// <summary>
    /// Calculates the overall OGC compliance score to validate 95%+ target achievement.
    /// This is the comprehensive compliance validation that should pass at 95%+.
    /// </summary>
    [Fact]
    public void FilterCapabilities_OverallCompliance_ShouldAchieve95PercentOGCCertificationTarget()
    {
        // Arrange
        var filterCapabilities = GetActualFilterCapabilities();

        // Act
        var complianceScore = CalculateComprehensiveOGCCompliance(filterCapabilities);

        // Assert
        complianceScore.Should().BeGreaterOrEqualTo(0.95,
            "WFS 2.0 implementation should achieve 95%+ OGC standards compliance for certification readiness");

        // Detailed breakdown for debugging
        var breakdown = GetComplianceBreakdown(filterCapabilities);
        foreach (var (category, passed, total) in breakdown)
        {
            var categoryScore = total > 0 ? (double)passed / total : 1.0;
            categoryScore.Should().BeGreaterOrEqualTo(0.90, // Allow some flexibility per category
                $"Compliance category '{category}' should achieve 90%+ ({passed}/{total} = {categoryScore:P})");
        }
    }

    /// <summary>
    /// Gets the actual FilterCapabilities from the Wfs20Handler to ensure we test real implementation.
    /// </summary>
    private static FilterCapabilities GetActualFilterCapabilities()
    {
        var type = typeof(Wfs20Handler);
        var method = type.GetMethod("BuildFilterCapabilities",
            BindingFlags.NonPublic | BindingFlags.Static);
        return (FilterCapabilities)method!.Invoke(null, null)!;
    }

    /// <summary>
    /// Calculates comprehensive OGC compliance score across all major capabilities.
    /// </summary>
    private static double CalculateComprehensiveOGCCompliance(FilterCapabilities capabilities)
    {
        var totalChecks = 0;
        var passedChecks = 0;

        // Core OGC conformance (weight: 40% - critical for basic compliance)
        totalChecks += 10;
        var constraints = capabilities.Conformance.Constraints.Select(c => c.Name).ToHashSet();
        var coreConstraints = new[]
        {
            "ImplementsQuery", "ImplementsAdHocQuery", "ImplementsResourceId",
            "ImplementsStandardFilter", "ImplementsMinSpatialFilter", "ImplementsSpatialFilter",
            "ImplementsMinTemporalFilter", "ImplementsTemporalFilter", "ImplementsFunctions",
            "ImplementsExtendedOperators"
        };
        passedChecks += coreConstraints.Count(constraint => constraints.Contains(constraint));

        // Temporal operator coverage (weight: 20% - critical gap to fix)
        totalChecks += 5;
        var temporalOps = capabilities.TemporalCapabilities?.TemporalOperators?.Operators?.Length ?? 0;
        if (temporalOps >= 10) passedChecks++; // Basic coverage
        if (temporalOps >= 13) passedChecks++; // Good coverage
        if (temporalOps >= 15) passedChecks++; // Complete Allen interval coverage
        if (temporalOps == 15) passedChecks++; // Exact compliance
        // Bonus point for proper naming (no T- prefix)
        var hasProperNaming = capabilities.TemporalCapabilities?.TemporalOperators?.Operators
            ?.All(op => !op.Name.StartsWith("T_")) ?? false;
        if (hasProperNaming) passedChecks++;

        // Spatial capabilities (weight: 15%)
        totalChecks += 3;
        var spatialOps = capabilities.SpatialCapabilities?.SpatialOperators?.Operators?.Length ?? 0;
        if (spatialOps >= 8) passedChecks++;   // Basic DE-9IM
        if (spatialOps >= 11) passedChecks++;  // Full DE-9IM
        var geometryOperands = capabilities.SpatialCapabilities?.GeometryOperands?.Operands?.Length ?? 0;
        if (geometryOperands >= 8) passedChecks++; // Comprehensive geometry support

        // Function capabilities (weight: 15% - critical for discovery)
        totalChecks += 3;
        var functions = capabilities.Functions?.Functions?.Length ?? 0;
        if (functions >= 25) passedChecks++;   // Good coverage
        if (functions >= 35) passedChecks++;   // Comprehensive coverage
        // Verify spatial functions are present
        var functionNames = capabilities.Functions?.Functions?.Select(f => f.Name).ToHashSet() ?? new HashSet<string>();
        var hasSpatialFunctions = RequiredSpatialFunctions.All(func => functionNames.Contains(func));
        if (hasSpatialFunctions) passedChecks++;

        // CQL2 and modern standards (weight: 10%)
        totalChecks += 2;
        var cql2Constraints = new[] { "ImplementsCQL2Text", "ImplementsCQL2JSON", "ImplementsCQL2SpatialOperators", "ImplementsCQL2TemporalOperators" };
        var cql2Support = cql2Constraints.Count(constraint => constraints.Contains(constraint));
        if (cql2Support >= 3) passedChecks++; // Good CQL2 support
        if (cql2Support == 4) passedChecks++; // Complete CQL2 support

        return (double)passedChecks / totalChecks;
    }

    /// <summary>
    /// Gets detailed compliance breakdown for debugging purposes.
    /// </summary>
    private static List<(string Category, int Passed, int Total)> GetComplianceBreakdown(FilterCapabilities capabilities)
    {
        var breakdown = new List<(string, int, int)>();
        var constraints = capabilities.Conformance.Constraints.Select(c => c.Name).ToHashSet();

        // Core conformance
        var coreConstraints = new[] { "ImplementsQuery", "ImplementsAdHocQuery", "ImplementsResourceId", "ImplementsStandardFilter" };
        var corePassed = coreConstraints.Count(c => constraints.Contains(c));
        breakdown.Add(("Core Conformance", corePassed, coreConstraints.Length));

        // Temporal capabilities
        var temporalOps = capabilities.TemporalCapabilities?.TemporalOperators?.Operators?.Length ?? 0;
        var temporalPassed = Math.Min(temporalOps / 5, 3); // 0-3 points based on coverage
        breakdown.Add(("Temporal Operators", temporalPassed, 3));

        // Spatial capabilities
        var spatialOps = capabilities.SpatialCapabilities?.SpatialOperators?.Operators?.Length ?? 0;
        var spatialPassed = Math.Min(spatialOps / 4, 3); // 0-3 points based on coverage
        breakdown.Add(("Spatial Operators", spatialPassed, 3));

        // Function capabilities
        var functions = capabilities.Functions?.Functions?.Length ?? 0;
        var functionsPassed = Math.Min(functions / 12, 3); // 0-3 points based on coverage
        breakdown.Add(("Functions", functionsPassed, 3));

        return breakdown;
    }
}
