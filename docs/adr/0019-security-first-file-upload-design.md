# ADR-0019: Security-First File Upload Design

## Status
Accepted

## Context

Honua Server accepts geospatial data file uploads from external users through multiple endpoints:
- Direct file uploads via REST API
- Shapefile zip archives
- GeoPackage databases
- CSV files with coordinate data

File uploads represent a significant security risk:
- **Malicious files**: Executable code disguised as data files
- **Resource exhaustion**: Large files consuming disk space and memory
- **Path traversal**: Malicious filenames attempting directory traversal
- **Content-type spoofing**: Incorrect MIME types hiding malicious content

Traditional upload validation often relies on single-layer checks (file extension or MIME type), which are easily bypassed by sophisticated attacks.

The system must balance security with usability for legitimate geospatial data uploads.

## Decision

Implement a **multi-layer security validation system** for all file uploads.

### Security Layers

**Layer 1: Transport Security**
- All uploads require HTTPS
- Request size limits enforced at middleware level
- Rate limiting on upload endpoints

**Layer 2: File Metadata Validation**
```csharp
internal static class FileUploadSecurity
{
    // Strict file size limits
    public const long DefaultMaxFileSizeBytes = 100 * 1024 * 1024; // 100MB
    public const int MaxSecurityScanSize = 10 * 1024 * 1024; // 10MB scan limit

    // Allowed MIME types (whitelist approach)
    private static readonly FrozenSet<string> _allowedMimeTypes = new[]
    {
        "application/zip",                    // Shapefiles
        "application/geopackage+sqlite3",     // GeoPackage
        "text/csv",                          // CSV data
        "application/json",                  // GeoJSON
        "text/plain"                         // Plain text formats
    }.ToFrozenSet();
}
```

**Layer 3: Content Signature Validation**
```csharp
// Magic number verification
private static readonly FrozenDictionary<string, byte[]> _maliciousSignatures = new()
{
    ["PE"] = new byte[] { 0x4D, 0x5A },        // Windows executables
    ["ELF"] = new byte[] { 0x7F, 0x45, 0x4C, 0x46 }, // Linux executables
    ["PS1"] = System.Text.Encoding.ASCII.GetBytes("#!/"), // Scripts
    ["BAT"] = System.Text.Encoding.ASCII.GetBytes("@echo"), // Batch files
    ["VBS"] = System.Text.Encoding.ASCII.GetBytes("Dim ")   // VBScript
};
```

**Layer 4: Geospatial Content Validation**
- ZIP files: Verify shapefile components (.shp, .shx, .dbf required)
- GeoPackage: Validate SQLite structure and spatial tables
- CSV: Validate coordinate columns and numeric formats
- GeoJSON: Parse and validate GeoJSON structure

**Layer 5: Sandbox Processing**
- All uploaded files processed in isolated environment
- No direct file system access from uploaded content
- Temporary storage with automatic cleanup

### Implementation Architecture

**Upload Flow**
```csharp
public async Task<IResult> UploadFileAsync(
    IFormFile file,
    IFileUploadValidator validator,
    IFileStorage storage)
{
    // Layer 1: Size and rate limiting (middleware)

    // Layer 2: Metadata validation
    var validationResult = await validator.ValidateAsync(file);
    if (!validationResult.IsValid)
        return Results.BadRequest(validationResult.Errors);

    // Layer 3: Content signature scanning
    using var stream = file.OpenReadStream();
    if (await ContainsMaliciousSignatures(stream))
        return Results.BadRequest("File content validation failed");

    // Layer 4: Format-specific validation
    stream.Position = 0;
    if (!await ValidateGeospatialFormat(stream, file.ContentType))
        return Results.BadRequest("Invalid geospatial data format");

    // Layer 5: Secure storage with isolation
    var fileId = await storage.StoreAsync(stream, validationResult.SafeFileName);

    return Results.Ok(new { FileId = fileId });
}
```

**Filename Sanitization**
```csharp
public static string SanitizeFileName(string fileName)
{
    // Remove path traversal attempts
    var sanitized = Path.GetFileName(fileName);

    // Replace invalid characters
    var invalidChars = Path.GetInvalidFileNameChars();
    foreach (var invalidChar in invalidChars)
    {
        sanitized = sanitized.Replace(invalidChar, '_');
    }

    // Limit length and ensure safe extension
    return $"{Guid.NewGuid()}_{sanitized}";
}
```

### Security Configuration

**Upload Limits**
```json
{
  "FileUpload": {
    "MaxFileSizeBytes": 104857600,     // 100MB
    "AllowedExtensions": [".zip", ".gpkg", ".csv", ".json"],
    "ScanSizeLimit": 10485760,         // 10MB
    "UploadTimeoutSeconds": 300        // 5 minutes
  }
}
```

**Rate Limiting**
- 10 uploads per minute per IP
- 100MB total per hour per IP
- Exponential backoff for repeated violations

## Consequences

### Positive
- **Security**: Multi-layer defense against malicious uploads
- **Reliability**: Prevents resource exhaustion attacks
- **Auditability**: Complete logging of upload attempts and validations
- **Compliance**: Meets security requirements for enterprise deployments
- **Flexibility**: Can add new validation layers without breaking existing code

### Negative
- **Performance**: Multiple validation steps increase upload processing time
- **Complexity**: More code paths to test and maintain
- **Resource Usage**: Content scanning requires additional CPU and memory
- **User Experience**: Legitimate files may be rejected due to strict validation

### Operational Impact

**Monitoring Requirements**
- Track upload success/failure rates
- Monitor validation layer performance
- Alert on unusual patterns or security violations

**Storage Considerations**
- Temporary storage for upload processing
- Automatic cleanup of failed uploads
- Backup strategy for validated files

**Incident Response**
- Logging includes security-relevant events
- Malicious upload attempts trigger security alerts
- Failed validations preserve evidence for analysis

### Development Guidelines

**Adding New File Types**
1. Update allowed MIME types whitelist
2. Implement format-specific validation
3. Add corresponding test cases
4. Update security documentation

**Testing Requirements**
- Test each validation layer independently
- Include malicious file samples in test suite
- Performance test with maximum file sizes
- Verify error handling and cleanup

### Future Enhancements
- Integration with external malware scanning services
- Machine learning-based content analysis
- Enhanced geospatial format validation
- Real-time upload progress monitoring