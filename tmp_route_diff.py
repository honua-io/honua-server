import re
import pathlib

root = pathlib.Path('C:/Users/mike/honua-io/honua-server')

def norm(path):
    path = path.strip()
    path = re.sub(r'\{([^}:]+):[^}]+\}', r'{\1}', path)
    if len(path) > 1 and path.endswith('/'):
        path = path.rstrip('/')
    return path

def parse_registry(path):
    text = path.read_text(encoding='utf-8')
    return [(m.group(1), norm(m.group(2))) for m in re.finditer(r'new\("(GET|POST|PUT|PATCH|DELETE)",\s*"([^"]+)"\)', text)]

def parse_map(path):
    text = path.read_text(encoding='utf-8')
    items=[]
    for m in re.finditer(r'\.(MapGet|MapPost|MapPut|MapDelete|MapPatch|MapMethods)\(\s*(?:"([^"]*)"|@"([^"]*)"|\$"([^"]*)")', text):
        meth = m.group(1)
        if meth == 'MapMethods':
            continue
        route = m.group(2) or m.group(3) or m.group(4) or ''
        items.append((meth.replace('Map','').upper(), norm(route)))
    return items

# OData
odata_reg = parse_registry(root / 'src/Honua.Server/EndpointRegistry.ODataApi.cs')
odata_calls = parse_map(root / 'src/Honua.Protocols.OData/Features/ODataEndpoints.cs')
rs=set(f'{m} {p}' for m,p in odata_reg)
cs=set(f'{m} {p}' for m,p in odata_calls)
print('OData missing', len(rs-cs))
for x in sorted(rs-cs):
    print(' -', x)
print('OData extra', len(cs-rs))
for x in sorted(cs-rs):
    print(' +', x)

# OGC API
target_ogc_routes = parse_registry(root / 'src/Honua.Server/EndpointRegistry.OgcApi.cs')
ogc_paths = [
    root / 'src/Honua.Protocols.OgcApi/Features/FeaturesEndpoints.cs',
    root / 'src/Honua.Protocols.OgcApi/Features/CoreEndpoints.cs',
    root / 'src/Honua.Protocols.OgcApi/Features/CollectionsEndpoints.cs',
    root / 'src/Honua.Protocols.OgcApi/Features/OgcFeaturesEndpoints.cs',
    root / 'src/Honua.Protocols.OgcApi/Processes/ProcessEndpoints.cs',
    root / 'src/Honua.Protocols.OgcApi/Processes/OgcProcessesEndpoints.cs',
    root / 'src/Honua.Protocols.OgcApi/Tiles/OgcTilesEndpoints.cs',
    root / 'src/Honua.Protocols.OgcApi/Tiles/TilesEndpoints.cs',
    root / 'src/Honua.Protocols.OgcApi/Records/OgcRecordsEndpoints.cs',
    root / 'src/Honua.Protocols.OgcApi/Coverages/OgcCoveragesEndpoints.cs',
    root / 'src/Honua.Protocols.OgcApi/Edr/EdrEndpoints.cs',
]
ogc_calls=[]
for p in ogc_paths:
    if p.exists():
        ogc_calls += parse_map(p)

rs=set(f'{m} {p}' for m,p in target_ogc_routes)
cs=set(f'{m} {p}' for m,p in ogc_calls)
print('OGC total missing', len(rs-cs))
for x in sorted(rs-cs):
    print(' -', x)
print('OGC total extra', len(cs-rs))
for x in sorted(cs-rs):
    print(' +', x)

# FeatureServer
fs_reg = parse_registry(root / 'src/Honua.Server/EndpointRegistry.FeatureServer.cs')
fs_files = list((root / 'src/Honua.Protocols.GeoServices/FeatureServer').glob('**/*Endpoints*.cs'))
# keep only endpoint source files
fs_calls=[]
for p in fs_files:
    if p.is_file() and 'MapFeatureServerNotImplementedEndpoints.cs' not in p.name:
        fs_calls += parse_map(p)

rs=set(f'{m} {p}' for m,p in fs_reg)
cs=set(f'{m} {p}' for m,p in fs_calls)
print('FeatureServer missing', len(rs-cs))
for x in sorted(rs-cs):
    print(' -', x)
print('FeatureServer extra', len(cs-rs))
for x in sorted(cs-rs):
    print(' +', x)
