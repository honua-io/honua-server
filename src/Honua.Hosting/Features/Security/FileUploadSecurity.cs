// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace Honua.Infrastructure.Security;

/// <summary>
/// Security utilities for file upload validation and sanitization.
/// Provides multi-layer defense against malicious file uploads.
/// </summary>
internal static class FileUploadSecurity
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
    private static readonly FrozenDictionary<string, byte[]> _maliciousSignatures = new Dictionary<string, byte[]>(
        StringComparer.Ordinal)
    {
        // PE executables
        ["PE"] = new byte[] { 0x4D, 0x5A }, // MZ header
        ["ELF"] = new byte[] { 0x7F, 0x45, 0x4C, 0x46 }, // ELF header
        // Script files that could be dangerous
        ["PS1"] = System.Text.Encoding.ASCII.GetBytes("#!/"),
        ["BAT"] = System.Text.Encoding.ASCII.GetBytes("@echo"),
        ["VBS"] = System.Text.Encoding.ASCII.GetBytes("Dim "),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Allowed MIME types for geospatial data files.
    /// </summary>
    private static readonly FrozenSet<string> _allowedMimeTypes = new[]
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
            // GeoParquet
            "application/vnd.apache.parquet",
            "application/x-parquet",
            // FlatGeobuf
            "application/vnd.flatgeobuf",
            "application/x-flatgeobuf",
            "application/flatgeobuf",
            // Raster formats
            "image/tiff",
            "image/geotiff",
            "image/png",
            "image/jpeg",
        }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Allowed file extensions for geospatial data.
    /// </summary>
    private static readonly FrozenSet<string> _allowedExtensions = new[]
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
            ".parquet", ".geoparquet", // GeoParquet
            ".fgb", // FlatGeobuf
            ".tif", ".tiff", // GeoTIFF raster
            ".png", ".jpg", ".jpeg", // Raster images (world-file georeferenced)
            ".pgw", ".jgw", ".tfw", ".wld", // Raster sidecar files (georeferencing); .prj already listed with shapefile components
        }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Dangerous file patterns that should be rejected.
    /// </summary>
    private static readonly Regex _dangerousPatternRegex = new(
        @"\.(exe|com|bat|cmd|scr|pif|vbs|js|jar|app|dmg|deb|rpm|msi|pkg)(\.|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly SearchValues<char> _pathSeparators = SearchValues.Create(new[] { '/', '\\' });

    /// <summary>
    /// Validates a file upload for security threats.
    /// </summary>
    public static Task<FileValidationResult> ValidateFileAsync(IFormFile file, CancellationToken cancellationToken = default)
        => ValidateFileAsync(file, null, null, cancellationToken);

    /// <summary>
    /// Validates a file upload for security threats with a custom size limit.
    /// </summary>
    public static async Task<FileValidationResult> ValidateFileAsync(IFormFile file, long? maxFileSizeBytes, CancellationToken cancellationToken = default)
        => await ValidateFileAsync(file, maxFileSizeBytes, null, cancellationToken);

    /// <summary>
    /// Validates a file upload for security threats with custom size and scan limits.
    /// </summary>
    public static async Task<FileValidationResult> ValidateFileAsync(
        IFormFile file,
        long? maxFileSizeBytes,
        long? maxScanSizeBytes,
        CancellationToken cancellationToken = default)
        => await ValidateFileAsync(
            file.FileName,
            file.ContentType,
            file.Length,
            () => file.OpenReadStream(),
            maxFileSizeBytes,
            maxScanSizeBytes,
            cancellationToken);

    /// <summary>
    /// Validates a file source using metadata plus a stream factory.
    /// </summary>
    public static async Task<FileValidationResult> ValidateFileAsync(
        string fileName,
        string? contentType,
        long fileSize,
        Func<Stream> openReadStream,
        long? maxFileSizeBytes,
        long? maxScanSizeBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(openReadStream);

        if (fileSize == 0)
        {
            return FileValidationResult.Invalid("No file provided or file is empty.");
        }

        // 1. Check file name for dangerous patterns
        var fileNameResult = ValidateFileName(fileName);
        if (!fileNameResult.IsValid)
        {
            return fileNameResult;
        }

        // 2. Check file extension
        var extensionResult = ValidateFileExtension(fileName);
        if (!extensionResult.IsValid)
        {
            return extensionResult;
        }

        // 3. Check MIME type
        var mimeTypeResult = ValidateMimeType(contentType);
        if (!mimeTypeResult.IsValid)
        {
            return mimeTypeResult;
        }

        // 4. Check file size
        var sizeResult = ValidateFileSize(fileSize, maxFileSizeBytes ?? DefaultMaxFileSizeBytes);
        if (!sizeResult.IsValid)
        {
            return sizeResult;
        }

        // 5. Check file content (magic number validation)
        var contentResult = await ValidateFileContentAsync(
            fileName,
            contentType,
            fileSize,
            openReadStream,
            maxScanSizeBytes,
            cancellationToken);
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

        var baseName = GetBaseFileName(fileName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            return FileValidationResult.Invalid("File name cannot be empty.");
        }

        var hasPathSeparators = fileName.Contains('/') || fileName.Contains('\\');
        var hasFakePathPrefix = fileName.StartsWith(@"C:\fakepath\", StringComparison.OrdinalIgnoreCase) ||
                                fileName.StartsWith("C:/fakepath/", StringComparison.OrdinalIgnoreCase);

        // Check for path traversal attempts
        if (fileName.Contains("..", StringComparison.Ordinal) || (hasPathSeparators && !hasFakePathPrefix))
        {
            return FileValidationResult.Invalid("File name contains invalid path characters.");
        }

        // Check for dangerous executable patterns
        if (_dangerousPatternRegex.IsMatch(baseName))
        {
            return FileValidationResult.Invalid("File type is not allowed for security reasons.");
        }

        // Check for overly long file names
        if (baseName.Length > 255)
        {
            return FileValidationResult.Invalid("File name is too long (maximum 255 characters).");
        }

        // Check for null bytes and other dangerous characters
        if (baseName.Any(c => c < 32 || c == 127 || "\"*:<>?|".Contains(c)))
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
        => await ValidateFileContentAsync(file, null, cancellationToken);

    /// <summary>
    /// Validates file content with a maximum scan size.
    /// </summary>
    public static async Task<FileValidationResult> ValidateFileContentAsync(
        IFormFile file,
        long? maxScanSizeBytes,
        CancellationToken cancellationToken = default)
        => await ValidateFileContentAsync(
            file.FileName,
            file.ContentType,
            file.Length,
            () => file.OpenReadStream(),
            maxScanSizeBytes,
            cancellationToken);

    /// <summary>
    /// Validates file content with a stream factory.
    /// </summary>
    public static async Task<FileValidationResult> ValidateFileContentAsync(
        string fileName,
        string? contentType,
        long fileSize,
        Func<Stream> openReadStream,
        long? maxScanSizeBytes,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(openReadStream);

            var scanLimit = ResolveScanLimit(fileSize, maxScanSizeBytes);
            if (scanLimit <= 0)
            {
                return FileValidationResult.Valid();
            }

            using var stream = openReadStream();

            // Read first few KB for magic number detection
            var signatureLength = (int)Math.Min(8192, scanLimit);
            var buffer = ArrayPool<byte>.Shared.Rent(signatureLength);
            try
            {
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, signatureLength), cancellationToken);

                if (bytesRead == 0)
                {
                    return FileValidationResult.Invalid("File appears to be empty or cannot be read.");
                }

                var signatureSlice = buffer.AsSpan(0, bytesRead);
                foreach (var signature in _maliciousSignatures.Values)
                {
                    if (ByteArrayStartsWith(signatureSlice, signature))
                    {
                        return FileValidationResult.Invalid("File contains a potentially malicious signature.");
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }

            // Additional content validation for text files
            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            if (IsTextFile(extension))
            {
                await using var textStream = openReadStream();
                var textValidationResult = await ValidateTextFileContentAsync(textStream, cancellationToken);
                if (!textValidationResult.IsValid)
                {
                    return textValidationResult;
                }
            }

            return FileValidationResult.Valid();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return FileValidationResult.Invalid("Error validating file content.");
        }
    }

    /// <summary>
    /// Validates text file content for malicious scripts or code.
    /// </summary>
    private static async Task<FileValidationResult> ValidateTextFileContentAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
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
            };

            var maxPatternLength = 0;
            foreach (var pattern in dangerousPatterns)
            {
                if (pattern.Length > maxPatternLength)
                {
                    maxPatternLength = pattern.Length;
                }
            }

            const int bufferSize = 4096;
            var buffer = ArrayPool<char>.Shared.Rent(bufferSize);
            var tail = string.Empty;

            try
            {
                while (true)
                {
                    var read = await reader.ReadAsync(buffer.AsMemory(0, bufferSize), cancellationToken);
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
            }
            finally
            {
                ArrayPool<char>.Shared.Return(buffer, clearArray: true);
            }

            return FileValidationResult.Valid();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // If we can't read as text, report it as invalid to be safe.
            // Binary files should not reach this method (IsTextFile check filters them).
            return FileValidationResult.Invalid("Unable to validate text file content.");
        }
    }

    private static long ResolveScanLimit(long fileLength, long? maxScanSizeBytes)
    {
        var limit = maxScanSizeBytes.GetValueOrDefault(MaxSecurityScanSize);
        if (limit <= 0)
        {
            limit = MaxSecurityScanSize;
        }

        if (fileLength <= 0)
        {
            return limit;
        }

        return Math.Min(fileLength, limit);
    }

    private sealed class LimitedReadStream : Stream
    {
        private readonly Stream _inner;
        private long _remaining;

        public LimitedReadStream(Stream inner, long maxBytes)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _remaining = maxBytes;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _remaining;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            var toRead = (int)Math.Min(count, _remaining);
            var read = _inner.Read(buffer, offset, toRead);
            _remaining -= read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            var toRead = (int)Math.Min(buffer.Length, _remaining);
            var read = _inner.Read(buffer[..toRead]);
            _remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            var toRead = (int)Math.Min(buffer.Length, _remaining);
            var read = await _inner.ReadAsync(buffer[..toRead], cancellationToken);
            _remaining -= read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override void Flush()
        {
            _inner.Flush();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }

    /// <summary>
    /// Checks if a byte array starts with a specific pattern.
    /// </summary>
    private static bool ByteArrayStartsWith(ReadOnlySpan<byte> array, ReadOnlySpan<byte> pattern)
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

    private static string GetBaseFileName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return string.Empty;
        }

        var lastSeparator = fileName.AsSpan().LastIndexOfAny(_pathSeparators);
        return lastSeparator >= 0 ? fileName[(lastSeparator + 1)..] : fileName;
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
        var baseName = GetBaseFileName(cleanedName);

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
            sanitized = nameWithoutExtension[..Math.Max(0, Math.Min(200 - extension.Length, nameWithoutExtension.Length))] + extension;
        }

        // Ensure it's not empty after sanitization
        return string.IsNullOrWhiteSpace(sanitized) ? "sanitized_file" : sanitized;
    }
}

/// <summary>
/// Result of file validation operations.
/// </summary>
internal sealed class FileValidationResult
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
