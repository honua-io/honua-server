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

Advanced FileGDB constructs such as domains, relationships, and attachments are tracked separately in `honua-server#451`.
