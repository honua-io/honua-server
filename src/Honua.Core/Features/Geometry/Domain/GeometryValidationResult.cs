// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.Geometry.Domain;

public sealed record GeometryValidationResult
{
    public required bool IsValid { get; init; }

    public ImmutableList<ValidationIssue> Errors { get; init; } = ImmutableList<ValidationIssue>.Empty;

    public ImmutableList<ValidationIssue> Warnings { get; init; } = ImmutableList<ValidationIssue>.Empty;

    public byte[]? RepairedWkb { get; init; }

    public bool WasRepaired => RepairedWkb != null;

    // A repair can change the geometry type, so a layer that declares one type must check the result again.
    public bool GeometryTypeChanged { get; init; }

    public GeometryStats? Stats { get; init; }

    public static GeometryValidationResult Success(GeometryStats? stats = null) => new()
    {
        IsValid = true,
        Stats = stats
    };

    public static GeometryValidationResult SuccessWithRepair(byte[] repairedWkb, IEnumerable<ValidationIssue>? warnings = null) => new()
    {
        IsValid = true,
        RepairedWkb = repairedWkb,
        Warnings = warnings?.ToImmutableList() ?? ImmutableList<ValidationIssue>.Empty
    };

    public static GeometryValidationResult Failure(params ValidationIssue[] errors) => new()
    {
        IsValid = false,
        Errors = errors.ToImmutableList()
    };

    public static GeometryValidationResult Failure(ValidationErrorCode code, string message) => new()
    {
        IsValid = false,
        Errors = ImmutableList.Create(new ValidationIssue(code, message))
    };

    public static GeometryValidationResult NullGeometry(bool isAllowed) => isAllowed
        ? Success()
        : Failure(ValidationErrorCode.NullGeometry, "Geometry cannot be null");
}

public sealed record ValidationIssue(ValidationErrorCode Code, string Message);

public sealed record GeometryStats
{
    public required string GeometryType { get; init; }

    public int VertexCount { get; init; }

    public int RingCount { get; init; }

    public long WkbSize { get; init; }

    public bool HasZ { get; init; }

    public bool HasM { get; init; }

    public int? Srid { get; init; }
}

public enum ValidationErrorCode
{
    None = 0,

    // Input validation errors (1xx)
    NullGeometry = 100,
    InvalidJsonStructure = 101,
    MissingCoordinates = 102,
    InvalidCoordinates = 103,
    CoordinateOutOfRange = 104,
    UnclosedRing = 105,
    InsufficientPoints = 106,

    // Security limits (2xx)
    TooManyVertices = 200,
    TooManyRings = 201,
    WkbTooLarge = 202,
    ValidationTimeout = 203,
    AttributeTooLong = 204,
    PayloadTooLarge = 205,

    // WKB validation errors (3xx)
    InvalidWkbStructure = 300,
    UnsupportedGeometryType = 301,
    SridMismatch = 302,
    EmptyGeometry = 303,

    // Topology errors (4xx)
    InvalidTopology = 400,
    SelfIntersection = 401,
    DuplicatePoints = 402,
    IncorrectOrientation = 403,
    HoleOutsideShell = 404,
    NestedHoles = 405,
    DisconnectedInterior = 406,

    // Repair errors (5xx)
    RepairFailed = 500,
    RepairResultedInEmpty = 501,
    RepairChangedType = 502
}
