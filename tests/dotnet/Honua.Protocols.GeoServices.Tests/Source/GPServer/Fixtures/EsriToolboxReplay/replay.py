import base64
import datetime
import inspect
import json
import struct
import sys
import traceback
from pathlib import Path

import arcgis
from arcgis.auth import EsriSession
from arcgis.geoprocessing import import_toolbox

config = json.load(sys.stdin)
root = Path(__file__).parent
origin = 'https://127.0.0.1:18464'
session = EsriSession(verify_cert=True, ca_bundles=[str(root / 'ca/root.crt')])
session.headers['X-API-Key'] = config['api_key']
report = {'at': datetime.datetime.now(datetime.timezone.utc).isoformat(),
          'source': config['source'], 'image': config['image'],
          'arcgis': arcgis.__version__,
          'tls_verified': True, 'checks': {}}
url = origin + '/rest/services/desktop_ui_features/GPServer'
metadata = session.get(url, params={'f':'json'}).json()
report['advertised_tasks'] = metadata.get('tasks', [])
try:
    import arcpy
    report['arcpy'] = arcpy.GetInstallInfo()['Version']
    toolbox = import_toolbox(url, gis=session)
    report['checks']['sdk_import'] = {'passed': True}
    name = 'honua_' + 'geometry.area'.encode().hex()
    tool = getattr(toolbox, name)
    # Literal 3x4 planar rectangle; standard WKB, independent of the server's NTS writer.
    ring = [(0.,0.), (3.,0.), (3.,4.), (0.,4.), (0.,0.)]
    wkb = struct.pack('<BIII', 1, 3, 1, len(ring)) + b''.join(struct.pack('<dd', *xy) for xy in ring)
    result = tool(wkb=base64.b64encode(wkb).decode(), srid=3857, future=False)
    report['sdk_result_type'] = type(result).__name__
    report['sdk_result'] = str(result)
    value = json.loads(result) if isinstance(result, str) else result
    if isinstance(value, dict):
        value = value['value']
    assert float(value) == 3 * 4, 'Planar rectangle area must equal width times height'
    report['checks']['sdk_remote_area'] = {'passed': True, 'expected':12, 'actual':float(value)}
except Exception as e:
    report['checks']['sdk_failure'] = {'passed':False, 'type':type(e).__name__, 'message':str(e)}

try:
    toolbox = arcpy.ImportToolbox(origin + '/services;desktop_ui_features', 'honua_gp_replay')
    names = arcpy.ListTools('*_honua_gp_replay') or []
    report['checks']['arcpy_import'] = {'passed':True, 'tools':names,
        'module_tools':list(getattr(toolbox, '__all__', [])), 'members':[n for n in dir(toolbox) if not n.startswith('_')]}
except Exception as e:
    report['checks']['arcpy_import'] = {'passed':False, 'type':type(e).__name__, 'message':str(e)}

text = json.dumps(report, indent=2).replace(config['api_key'], '[redacted]')
(root / (config['label'] + '.json')).write_text(text + '\n', encoding='utf-8')
print(json.dumps({'source':report['source'], 'checks':{k:{a:b for a,b in v.items() if a not in ['members','module_tools','tools']} for k,v in report['checks'].items()}}, indent=2))
