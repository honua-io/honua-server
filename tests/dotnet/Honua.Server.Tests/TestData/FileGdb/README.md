These File Geodatabase fixtures are used for Honua import integration tests.

Sources:
- `testopenfilegdb.gdb.zip`
- `sparse.gdb.zip`

Both files were copied from the GDAL test corpus:
- Repository: `https://github.com/OSGeo/gdal`
- Source directory: `autotest/ogr/data/filegdb/`

License:
- GDAL/OGR is distributed under an MIT-style permissive license. See:
  `https://raw.githubusercontent.com/OSGeo/gdal/master/LICENSE.TXT`

Intended use:
- `testopenfilegdb.gdb.zip`: common happy-path FileGDB import/preview coverage for `honua-server#433`
- `sparse.gdb.zip`: secondary parser coverage and regression testing
- `domain-coded.gdb.zip`, `domain-range.gdb.zip`, `relationship-class.gdb.zip`: generated with GDAL's
  OpenFileGDB write driver (`ogr.CreateCodedFieldDomain` / `ogr.CreateRangeFieldDomain` /
  `gdal.Relationship`) specifically to exercise `FileGdbAdvancedConstructs.DetectWarnings`
  (honua-server#4422) with real advanced constructs. `testopenfilegdb.gdb.zip` and `sparse.gdb.zip`
  contain none of these constructs (verified by grepping their raw `GDB_Items` table for the
  Esri type names), so they exercise the "no warnings" path instead.

Advanced FileGDB constructs such as domains, relationships, and attachments are tracked separately in `honua-server#451`.
