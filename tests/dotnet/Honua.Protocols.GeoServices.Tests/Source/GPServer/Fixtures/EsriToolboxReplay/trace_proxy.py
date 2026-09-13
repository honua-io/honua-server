import http.client
import gzip
import json
import xml.etree.ElementTree as ET
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlsplit

class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def relay(self):
        length = int(self.headers.get('Content-Length', 0))
        if length > 2 * 1024 * 1024:
            self.send_error(413)
            return
        body = self.rfile.read(length)
        operation = None
        if 'xml' in self.headers.get('Content-Type', ''):
            try:
                xml = ET.fromstring(body)
                soap_body = next(e for e in xml if e.tag.endswith('}Body'))
                operation = next(iter(soap_body)).tag
            except (ET.ParseError, StopIteration):
                operation = 'unparsed'
        headers = {k:v for k,v in self.headers.items() if k.lower() not in ['connection', 'transfer-encoding']}
        conn = http.client.HTTPConnection('honua', 8080, timeout=180)
        conn.request(self.command, self.path, body, headers)
        response = conn.getresponse()
        data = response.read()
        row = {'method':self.command,'path':urlsplit(self.path).path,'operation':operation,'status':response.status}
        if urlsplit(self.path).path.endswith('/submitJob'):
            try:
                payload = json.loads(gzip.decompress(data) if response.getheader('Content-Encoding') == 'gzip' else data)
                row['response_keys'] = list(payload)
                error = payload.get('error')
                if error:
                    row['error'] = {k:error[k] for k in ['code','message'] if k in error}
            except (ValueError, AttributeError):
                pass
        print(json.dumps(row), flush=True)
        self.send_response(response.status)
        for key,value in response.getheaders():
            if key.lower() not in ['connection','transfer-encoding','content-length']:
                self.send_header(key,value)
        self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        self.wfile.write(data)
        conn.close()

    do_GET = relay
    do_POST = relay

ThreadingHTTPServer(('0.0.0.0',8082), Handler).serve_forever()
