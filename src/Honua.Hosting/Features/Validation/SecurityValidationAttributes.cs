// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Honua.Core.Features.Shared.Models;
using DataAnnotationsValidationResult = System.ComponentModel.DataAnnotations.ValidationResult;

namespace Honua.Infrastructure.Validation;

/// <summary>
/// Validation attribute that ensures a string contains only safe characters for SQL table/column names.
/// Prevents SQL injection through identifiers.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class SafeSqlIdentifierAttribute : ValidationAttribute
{
    private static readonly Regex _sqlIdentifierRegex = new(@"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.Compiled);
    private static readonly FrozenSet<string> _reservedWords = new[]
        {
            "SELECT", "FROM", "WHERE", "INSERT", "UPDATE", "DELETE", "CREATE", "DROP", "ALTER",
            "TABLE", "INDEX", "VIEW", "DATABASE", "SCHEMA", "USER", "GROUP", "ORDER", "BY",
            "HAVING", "UNION", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "ON",
            "NULL", "TRUE", "FALSE", "AND", "OR", "NOT", "IN", "EXISTS", "BETWEEN", "LIKE"
        }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    protected override DataAnnotationsValidationResult IsValid(object? value, ValidationContext validationContext)
    {
        if (value is not string stringValue)
        {
            return new DataAnnotationsValidationResult("Value must be a string.");
        }

        if (string.IsNullOrWhiteSpace(stringValue))
        {
            return new DataAnnotationsValidationResult("SQL identifier cannot be empty.");
        }

        if (stringValue.Length > 63) // PostgreSQL identifier limit
        {
            return new DataAnnotationsValidationResult("SQL identifier cannot exceed 63 characters.");
        }

        if (!_sqlIdentifierRegex.IsMatch(stringValue))
        {
            return new DataAnnotationsValidationResult("SQL identifier can only contain letters, numbers, and underscores, and must start with a letter or underscore.");
        }

        // Check for SQL reserved words
        if (IsReservedWord(stringValue))
        {
            return new DataAnnotationsValidationResult($"'{stringValue}' is a reserved SQL keyword and cannot be used as an identifier.");
        }

        return DataAnnotationsValidationResult.Success!;
    }

    private static bool IsReservedWord(string identifier)
    {
        if (_reservedWords.Contains(identifier))
        {
            return true;
        }

        var underscoreIndex = identifier.IndexOf('_');
        if (underscoreIndex <= 0)
        {
            return false;
        }

        var firstToken = identifier[..underscoreIndex];
        return _reservedWords.Contains(firstToken);
    }
}

/// <summary>
/// Validation attribute that ensures a geometry SRID is within valid ranges.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class ValidSridAttribute : ValidationAttribute
{
    protected override DataAnnotationsValidationResult IsValid(object? value, ValidationContext validationContext)
    {
        if (value == null)
        {
            return DataAnnotationsValidationResult.Success!; // Nullable SRID is allowed
        }

        if (value is not int srid)
        {
            return new DataAnnotationsValidationResult("SRID must be an integer.");
        }

        // Valid SRID ranges based on EPSG standards
        if (srid < 0 || srid > 999999)
        {
            return new DataAnnotationsValidationResult(ErrorMessages.RangeValidation.SridRange);
        }

        // Common invalid SRIDs
        if (srid == 0)
        {
            return DataAnnotationsValidationResult.Success!; // 0 is valid (unknown/undefined)
        }

        return DataAnnotationsValidationResult.Success!;
    }
}

/// <summary>
/// Validation attribute that ensures a file extension is in the allowed list.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class AllowedFileExtensionAttribute : ValidationAttribute
{
    private readonly HashSet<string> _allowedExtensions;

    public AllowedFileExtensionAttribute(params string[] allowedExtensions)
    {
        _allowedExtensions = new HashSet<string>(allowedExtensions, StringComparer.OrdinalIgnoreCase);
    }

    protected override DataAnnotationsValidationResult IsValid(object? value, ValidationContext validationContext)
    {
        if (value is not string fileName)
        {
            return new DataAnnotationsValidationResult("File name must be a string.");
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return new DataAnnotationsValidationResult("File name cannot be empty.");
        }

        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(extension))
        {
            return new DataAnnotationsValidationResult("File must have an extension.");
        }

        if (!_allowedExtensions.Contains(extension))
        {
            return new DataAnnotationsValidationResult($"File extension '{extension}' is not allowed. Allowed extensions: {string.Join(", ", _allowedExtensions)}");
        }

        return DataAnnotationsValidationResult.Success!;
    }
}

/// <summary>
/// Validation attribute that ensures coordinate values are within valid geographic bounds.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class ValidCoordinateAttribute : ValidationAttribute
{
    public CoordinateType CoordinateType { get; }

    public ValidCoordinateAttribute(CoordinateType coordinateType)
    {
        CoordinateType = coordinateType;
    }

    protected override DataAnnotationsValidationResult IsValid(object? value, ValidationContext validationContext)
    {
        if (value == null)
        {
            return DataAnnotationsValidationResult.Success!; // Nullable coordinates allowed
        }

        if (value is not double coordinate)
        {
            return new DataAnnotationsValidationResult("Coordinate must be a number.");
        }

        if (double.IsNaN(coordinate) || double.IsInfinity(coordinate))
        {
            return new DataAnnotationsValidationResult("Coordinate cannot be NaN or infinity.");
        }

        return CoordinateType switch
        {
            CoordinateType.Longitude when coordinate < -180.0 || coordinate > 180.0 =>
                new DataAnnotationsValidationResult(ErrorMessages.RangeValidation.LongitudeRange),
            CoordinateType.Latitude when coordinate < -90.0 || coordinate > 90.0 =>
                new DataAnnotationsValidationResult(ErrorMessages.RangeValidation.LatitudeRange),
            _ => DataAnnotationsValidationResult.Success!
        };
    }
}

/// <summary>
/// Types of coordinate validation.
/// </summary>
public enum CoordinateType
{
    Longitude,
    Latitude
}

/// <summary>
/// Validation attribute that ensures a string doesn't contain dangerous characters for XSS prevention.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class SafeStringAttribute : ValidationAttribute
{
    private static readonly Regex _dangerousPatternRegex = new(@"[<>&""'\x00-\x1f\x7f-\x9f]", RegexOptions.Compiled);

    protected override DataAnnotationsValidationResult IsValid(object? value, ValidationContext validationContext)
    {
        if (value == null)
        {
            return DataAnnotationsValidationResult.Success!; // Null strings are allowed
        }

        if (value is not string stringValue)
        {
            return new DataAnnotationsValidationResult("Value must be a string.");
        }

        if (_dangerousPatternRegex.IsMatch(stringValue))
        {
            return new DataAnnotationsValidationResult("String contains potentially dangerous characters.");
        }

        return DataAnnotationsValidationResult.Success!;
    }
}

/// <summary>
/// Validation attribute for WHERE clause strings to prevent SQL injection.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class SafeWhereClauseAttribute : ValidationAttribute
{
    private static readonly Regex _dangerousPatternRegex = new(
        @"(;\s*(?:DROP|DELETE|UPDATE|INSERT|CREATE|ALTER|EXEC|EXECUTE|DECLARE|xp_|sp_))|(\-\-)|(/\*)|(\*/)|(\bUNION\b)|(\bOR\b\s+\d+\s*=\s*\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    protected override DataAnnotationsValidationResult IsValid(object? value, ValidationContext validationContext)
    {
        if (value == null)
        {
            return DataAnnotationsValidationResult.Success!; // Null WHERE clauses are allowed
        }

        if (value is not string whereClause)
        {
            return new DataAnnotationsValidationResult("WHERE clause must be a string.");
        }

        if (string.IsNullOrWhiteSpace(whereClause))
        {
            return DataAnnotationsValidationResult.Success!; // Empty WHERE clauses are allowed
        }

        if (whereClause.Length > 4000) // Reasonable limit for WHERE clauses
        {
            return new DataAnnotationsValidationResult("WHERE clause is too long (maximum 4000 characters).");
        }

        if (_dangerousPatternRegex.IsMatch(whereClause))
        {
            return new DataAnnotationsValidationResult("WHERE clause contains potentially dangerous SQL patterns.");
        }

        return DataAnnotationsValidationResult.Success!;
    }
}

/// <summary>
/// Validation attribute for pagination parameters.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class ValidPaginationAttribute : ValidationAttribute
{
    public int MaxValue { get; }

    public ValidPaginationAttribute(int maxValue = 10000)
    {
        MaxValue = maxValue;
    }

    protected override DataAnnotationsValidationResult IsValid(object? value, ValidationContext validationContext)
    {
        if (value == null)
        {
            return DataAnnotationsValidationResult.Success!; // Nullable pagination parameters are allowed
        }

        if (value is not int intValue)
        {
            return new DataAnnotationsValidationResult("Pagination parameter must be an integer.");
        }

        if (intValue < 0)
        {
            return new DataAnnotationsValidationResult("Pagination parameter cannot be negative.");
        }

        if (intValue > MaxValue)
        {
            return new DataAnnotationsValidationResult($"Pagination parameter cannot exceed {MaxValue}.");
        }

        return DataAnnotationsValidationResult.Success!;
    }
}
