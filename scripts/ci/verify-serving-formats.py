import argparse
import json
from pathlib import Path
import sqlite3
from PIL import Image, ImageChops

LEGEND_LABEL_START_X = 34


def verify_import(result, feature_count):
    if result.get('success') is not True or result.get('featureCount') != feature_count:
        raise ValueError('Import did not preserve the expected feature count')


def verify_legend(path):
    with Image.open(path) as image:
        visible = Image.alpha_composite(Image.new('RGBA', image.size, 'white'), image.convert('RGBA')).convert('RGB')
        background = visible.getpixel((visible.width - 1, 0))
        # The requested swatch ends at x=28; only label glyphs can paint x>=34.
        label = visible.crop((min(LEGEND_LABEL_START_X, visible.width), 0, visible.width, visible.height))
        if image.width <= LEGEND_LABEL_START_X or ImageChops.difference(label, Image.new('RGB', label.size, background)).getbbox() is None:
            raise ValueError('Legend label glyphs did not render')


def verify_geopackage(path, feature_count=3):
    with sqlite3.connect(f'file:{path}?mode=ro', uri=True) as database:
        if database.execute('PRAGMA integrity_check').fetchone() != ('ok',):
            raise ValueError('GeoPackage failed its integrity check')
        tables = database.execute("SELECT table_name FROM gpkg_contents WHERE data_type='features'").fetchall()
        if len(tables) != 1:
            raise ValueError('GeoPackage must contain one feature table')
        table = tables[0][0].replace('"', '""')
        if database.execute(f'SELECT count(*) FROM "{table}"').fetchone()[0] != feature_count:
            raise ValueError('GeoPackage did not preserve the expected feature count')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('kind', choices=['import', 'legend', 'gpkg'])
    parser.add_argument('path', type=Path)
    parser.add_argument('--feature-count', type=int, default=3)
    args = parser.parse_args()
    if args.kind == 'import':
        verify_import(json.loads(args.path.read_text()), args.feature_count)
    elif args.kind == 'legend':
        verify_legend(args.path)
    else:
        verify_geopackage(args.path, args.feature_count)


if __name__ == '__main__':
    main()
