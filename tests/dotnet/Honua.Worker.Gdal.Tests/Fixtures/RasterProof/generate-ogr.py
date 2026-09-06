"""Input-only, hand specified EPSG:4326 multilayer dataset for source.ogr."""
from pathlib import Path
from osgeo import ogr, osr
ogr.UseExceptions()
path = Path(__file__).with_name('survey.gpkg')
if path.exists():
    path.unlink()
ds = ogr.GetDriverByName('GPKG').CreateDataSource(str(path))
srs = osr.SpatialReference()
srs.ImportFromEPSG(4326)
srs.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
for name, rows in [
    ('decoy', [(99, 'wrong layer', 'POINT Z (80 70 60)', 999)]),
    ('survey', [(11, 'Kīlauea 日本', 'POINT Z (-155.25 19.5 120.125)', 12.5),
                (12, None, 'LINESTRING Z (-156 20 1.25,-155 21 2.5)', -3.75),
                (13, 'area', 'POLYGON Z ((-154 18 0,-153 18 1,-153 19 2,-154 18 0))', None),
                (14, 'no geometry', None, 0)])]:
    layer = ds.CreateLayer(name, srs, ogr.wkbUnknown)
    for field, kind in [('key', ogr.OFTInteger), ('name', ogr.OFTString), ('reading', ogr.OFTReal)]:
        layer.CreateField(ogr.FieldDefn(field, kind))
    for key, label, wkt, value in rows:
        feature = ogr.Feature(layer.GetLayerDefn())
        feature.SetField('key', key)
        if label is not None:
            feature.SetField('name', label)
        if value is not None:
            feature.SetField('reading', value)
        if wkt is not None:
            feature.SetGeometry(ogr.CreateGeometryFromWkt(wkt))
        layer.CreateFeature(feature)
ds = None
