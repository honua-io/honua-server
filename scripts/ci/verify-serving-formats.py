import argparse
import json
from pathlib import Path
import sqlite3
from PIL import Image


def verify_import(result, feature_count):
    if result.get('success') is not True or result.get('featureCount') != feature_count:
        raise ValueError('Import did not preserve the expected feature count')


def verify_legend(path):
    with Image.open(path) as image:
        alpha = image.convert('RGBA').getchannel('A')
        # The requested swatch ends at x=28; only label glyphs can paint x>=34.
        if image.width <= 34 or alpha.crop((34, 0, image.width, image.height)).getbbox() is None:
            raise ValueError('Legend label glyphs did not render')


def verify_geopackage(path):
    with sqlite3.connect(f'file:{path}?mode=ro', uri=True) as database:
        if database.execute('PRAGMA integrity_check').fetchone() != ('ok',):
            raise ValueError('GeoPackage failed its integrity check')
        if database.execute("SELECT count(*) FROM gpkg_contents WHERE data_type='features'").fetchone()[0] < 1:
            raise ValueError('GeoPackage contains no feature table')


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
        verify_geopackage(args.path)


if __name__ == '__main__':
    main()
