// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.Server.Features.Infrastructure.Security;

/// <summary>
/// Security utilities for file upload validation and sanitization.
/// Provides multi-layer defense against malicious file uploads.
/// </summary>
public static class FileUploadSecurity
{
    /// <summary>
    /// Default maximum file size allowed for validation (100MB).
    /// </summary>
    public const long DefaultMaxFileSizeBytes = 100 * 1024 * 1024;

    /// <summary>
    /// Maximum file size for security scanning (10MB).
    /// Files larger than this should be processed in chunks.
    /// </summary>
    public const int MaxSecurityScanSize = 10 * 1024 * 1024;

    /// <summary>
    /// Known malicious file signatures (magic numbers).
    /// </summary>
    private static readonly Dictionary<string, byte[]> _maliciousSignatures = new()
    {
        // PE executables
        ["PE"] = new byte[] { 0x4D, 0x5A }, // MZ header
        ["ELF"] = new byte[] { 0x7F, 0x45, 0x4C, 0x46 }, // ELF header
        // Script files that could be dangerous
        ["PS1"] = System.Text.Encoding.ASCII.GetBytes("#!/"),
        ["BAT"] = System.Text.Encoding.ASCII.GetBytes("@echo"),
        ["VBS"] = System.Text.Encoding.ASCII.GetBytes("Dim "),
    };

    /// <summary>
    /// Allowed MIME types for geospatial data files.
    /// </summary>
    private static readonly HashSet<string> _allowedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Geospatial formats
        "application/octet-stream", // For shapefiles and other binary formats
        "application/zip", // For zipped shapefiles
        "application/x-zip-compressed",
        "application/geopackage+sqlite3",
        "application/x-sqlite3",
        "application/wkt",
        "text/csv",
        "application/vnd.ms-excel",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/geo+json",
        "application/json",
        "text/plain",
        "application/gpx+xml",
        "text/xml",
        "application/xml",
        // KML/KMZ
        "application/vnd.google-earth.kml+xml",
        "application/vnd.google-earth.kmz",
    };

    /// <summary>
    /// Allowed file extensions for geospatial data.
    /// </summary>
    private static readonly HashSet<string> _allowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".shp", ".dbf", ".shx", ".prj", ".cpg", ".sbn", ".sbx", ".fbn", ".fbx", // Shapefile components
        ".zip", // Zipped shapefiles
        ".csv", ".tsv", ".txt",
        ".xls", ".xlsx",
        ".geojson", ".json",
        ".gpkg",
        ".wkt",
        ".gpx",
        ".kml", ".kmz",
        ".gml", ".xml",
        ".tab", ".mif", ".mid", // MapInfo formats
        ".gdb", // File geodatabase (folder)
    };

    /// <summary>
    /// Dangerous file patterns that should be rejected.
    /// </summary>
    private static readonly Regex _dangerousPatternRegex = new(
        @"\.(exe|com|bat|cmd|scr|pif|vbs|js|jar|app|dmg|deb|rpm|msi|pkg)(\.|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Validates a file upload for security threats.
    /// </summary>
    public static Task<FileValidationResult> ValidateFileAsync(IFormFile file, CancellationToken cancellationToken = default)
        => ValidateFileAsync(file, null, cancellationToken);

    /// <summary>
    /// Validates a file upload for security threats with a custom size limit.
    /// </summary>
    public static async Task<FileValidationResult> ValidateFileAsync(IFormFile file, long? maxFileSizeBytes, CancellationToken cancellationToken = default)
    {
        if (file == null || file.Length == 0)
        {
            return FileValidationResult.Invalid("No file provided or file is empty.");
        }

        // 1. Check file name for dangerous patterns
        var fileNameResult = ValidateFileName(file.FileName);
        if (!fileNameResult.IsValid)
        {
            return fileNameResult;
        }

        // 2. Check file extension
        var extensionResult = ValidateFileExtension(file.FileName);
        if (!extensionResult.IsValid)
        {
            return extensionResult;
        }

        // 3. Check MIME type
        var mimeTypeResult = ValidateMimeType(file.ContentType);
        if (!mimeTypeResult.IsValid)
        {
            return mimeTypeResult;
        }

        // 4. Check file size
        var sizeResult = ValidateFileSize(file.Length, maxFileSizeBytes ?? DefaultMaxFileSizeBytes);
        if (!sizeResult.IsValid)
        {
            return sizeResult;
        }

        // 5. Check file content (magic number validation)
        var contentResult = await ValidateFileContentAsync(file, cancellationToken);
        if (!contentResult.IsValid)
        {
            return contentResult;
        }

        return FileValidationResult.Valid();
    }

    /// <summary>
    /// Validates the file name for dangerous patterns and characters.
    /// </summary>
    public static FileValidationResult ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return FileValidationResult.Invalid("File name cannot be empty.");
        }

        // Check for path traversal attempts
        if (fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
        {
            return FileValidationResult.Invalid("File name contains invalid path characters.");
        }

        // Check for dangerous executable patterns
        if (_dangerousPatternRegex.IsMatch(fileName))
        {
            return FileValidationResult.Invalid("File type is not allowed for security reasons.");
        }

        // Check for overly long file names
        if (fileName.Length > 255)
        {
            return FileValidationResult.Invalid("File name is too long (maximum 255 characters).");
        }

        // Check for null bytes and other dangerous characters
        if (fileName.Any(c => c < 32 || c == 127 || "\"*:<>?|".Contains(c)))
        {
            return FileValidationResult.Invalid("File name contains invalid characters.");
        }

        return FileValidationResult.Valid();
    }

    /// <summary>
    /// Validates the file extension against allowed types.
    /// </summary>
    public static FileValidationResult ValidateFileExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(extension))
        {
            return FileValidationResult.Invalid("File must have an extension.");
        }

        if (!_allowedExtensions.Contains(extension))
        {
            return FileValidationResult.Invalid($"File extension '{extension}' is not allowed. Allowed extensions: {string.Join(", ", _allowedExtensions)}");
        }

        return FileValidationResult.Valid();
    }

    /// <summary>
    /// Validates the MIME type against allowed types.
    /// </summary>
    public static FileValidationResult ValidateMimeType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return FileValidationResult.Invalid("MIME type is required.");
        }

        // Remove charset and other parameters
        var mimeType = contentType.Split(';')[0].Trim();

        if (!_allowedMimeTypes.Contains(mimeType))
        {
            return FileValidationResult.Invalid($"MIME type '{mimeType}' is not allowed.");
        }

        return FileValidationResult.Valid();
    }

    /// <summary>
    /// Validates the file size against configured limits.
    /// </summary>
    public static FileValidationResult ValidateFileSize(long fileSize, long maxFileSizeBytes = DefaultMaxFileSizeBytes)
    {
        if (fileSize <= 0)
        {
            return FileValidationResult.Invalid("File size must be greater than zero.");
        }

        if (fileSize > maxFileSizeBytes)
        {
            return FileValidationResult.Invalid($"File size exceeds maximum allowed size of {maxFileSizeBytes:N0} bytes.");
        }

        return FileValidationResult.Valid();
    }

    /// <summary>
    /// Validates file content by checking magic numbers and scanning for malicious patterns.
    /// </summary>
    public static async Task<FileValidationResult> ValidateFileContentAsync(IFormFile file, CancellationToken cancellationToken = default)
    {
        try
        {
            using var stream = file.OpenReadStream();

            // Read first few KB for magic number detection
            var buffer = new byte[Math.Min(8192, (int)file.Length)];
            var bytesRead = await stream.ReadAsync(buffer, cancellationToken);

            if (bytesRead == 0)
            {
                return FileValidationResult.Invalid("File appears to be empty or cannot be read.");
            }

            // Check for known malicious signatures
            foreach (var signature in _maliciousSignatures.Values)
            {
                if (ByteArrayStartsWith(buffer, signature))
                {
                    return FileValidationResult.Invalid("File contains a potentially malicious signature.");
                }
            }

            // Additional content validation for text files
            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (IsTextFile(extension))
            {
                var textValidationResult = await ValidateTextFileContentAsync(stream, cancellationToken);
                if (!textValidationResult.IsValid)
                {
                    return textValidationResult;
                }
            }

            return FileValidationResult.Valid();
        }
        catch (Exception ex)
        {
            return FileValidationResult.Invalid($"Error validating file content: {ex.Message}");
        }
    }

    /// <summary>
    /// Validates text file content for malicious scripts or code.
    /// </summary>
    private static async Task<FileValidationResult> ValidateTextFileContentAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            if (stream.CanSeek)
            {
                stream.Position = 0;
            }

            using var reader = new StreamReader(stream, leaveOpen: true);

            // Check for script patterns
            var dangerousPatterns = new[]
            {
                "<script",
                "javascript:",
                "vbscript:",
                "data:text/html",
                "eval(",
                "exec(",
                "system(",
                "shell_exec",
                "passthru(",
                "<?php",
                "<%",
                "{{",
                "function(",
                "var ",
                "const ",
                "let ",
            };

            var maxPatternLength = 0;
            foreach (var pattern in dangerousPatterns)
            {
                if (pattern.Length > maxPatternLength)
                {
                    maxPatternLength = pattern.Length;
                }
            }

            var buffer = new char[4096];
            var tail = string.Empty;

            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                var chunk = new string(buffer, 0, read);
                var combined = string.Concat(tail, chunk);

                foreach (var pattern in dangerousPatterns)
                {
                    if (combined.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        return FileValidationResult.Invalid("File contains potentially dangerous script content.");
                    }
                }

                if (maxPatternLength > 1)
                {
                    var tailLength = Math.Min(maxPatternLength - 1, combined.Length);
                    tail = combined[^tailLength..];
                }
                else
                {
                    tail = string.Empty;
                }
            }

            return FileValidationResult.Valid();
        }
        catch
        {
            // If we can't read as text, that's fine - it might be binary
            return FileValidationResult.Valid();
        }
    }

    /// <summary>
    /// Checks if a byte array starts with a specific pattern.
    /// </summary>
    private static bool ByteArrayStartsWith(byte[] array, byte[] pattern)
    {
        if (array.Length < pattern.Length)
            return false;

        for (int i = 0; i < pattern.Length; i++)
        {
            if (array[i] != pattern[i])
                return false;
        }

        return true;
    }

    /// <summary>
    /// Determines if a file extension indicates a text file.
    /// </summary>
    private static bool IsTextFile(string extension)
    {
        var textExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".csv", ".tsv", ".json", ".xml", ".kml", ".gml", ".gpx", ".prj"
        };

        return textExtensions.Contains(extension);
    }

    /// <summary>
    /// Sanitizes a file name for safe storage.
    /// </summary>
    public static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "unnamed_file";
        }

        var cleanedName = fileName.Replace("\0", string.Empty, StringComparison.Ordinal);
        cleanedName = new string(cleanedName.Where(c => !char.IsControl(c)).ToArray());
        var baseName = Path.GetFileName(cleanedName);

        // Remove dangerous characters
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(baseName.Where(c => !invalidChars.Contains(c)
            && c >= 32
            && c != '<'
            && c != '>'
            && c != '"'
            && c != '\''
            && c != ':'
            && c != '|'
            && c != '?'
            && c != '*').ToArray());
        sanitized = sanitized.Replace("\0", string.Empty, StringComparison.Ordinal);

        // Collapse path traversal patterns
        while (sanitized.Contains("..", StringComparison.Ordinal))
        {
            sanitized = sanitized.Replace("..", ".", StringComparison.Ordinal);
        }

        // Trim leading/trailing dots that can hide names or imply traversal
        sanitized = sanitized.Trim('.');

        // Ensure it's not too long
        if (sanitized.Length > 200)
        {
            var extension = Path.GetExtension(sanitized);
            var nameWithoutExtension = Path.GetFileNameWithoutExtension(sanitized);
            sanitized = nameWithoutExtension[..Math.Min(200 - extension.Length, nameWithoutExtension.Length)] + extension;
        }

        // Ensure it's not empty after sanitization
        return string.IsNullOrWhiteSpace(sanitized) ? "sanitized_file" : sanitized;
    }
}

/// <summary>
/// Result of file validation operations.
/// </summary>
public sealed class FileValidationResult
{
    public bool IsValid { get; private set; }
    public string? ErrorMessage { get; private set; }

    private FileValidationResult(bool isValid, string? errorMessage = null)
    {
        IsValid = isValid;
        ErrorMessage = errorMessage;
    }

    public static FileValidationResult Valid() => new(true);
    public static FileValidationResult Invalid(string errorMessage) => new(false, errorMessage);
}
