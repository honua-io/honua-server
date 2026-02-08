# Import Process Issues

Use this guide when file imports fail, hang, or produce unexpected results.

**Scope**: Import limits, formats, and common failure modes.

---

## Quick Checks

**Supported formats**:
```bash
curl http://localhost:8080/api/v1/admin/import/formats
```

**Current import limits**:
```bash
curl http://localhost:8080/api/v1/admin/import/limits
```

**Check recent jobs**:
```bash
curl http://localhost:8080/api/v1/admin/import/jobs
```

---

## Common Causes

- File size exceeds configured limits.
- Invalid geometry or unsupported CRS in the input.
- Network timeouts on large uploads.
- Missing PostGIS extension.

---

## Fixes to Try

- Reduce file size or split into smaller chunks.
- Validate geometry before import.
- Increase `Limits__Imports__MaxImportSize` if appropriate.
- Use the Admin UI to validate and preview before import.

---

## Related Docs

- [Admin UI Import Guide](../../user/admin-ui/import-guide.md)
- [Security Configuration](../SECURITY_CONFIGURATION.md)
