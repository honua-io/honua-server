#!/usr/bin/env python3
"""Write explicitly synthetic TRX for offline shard-supervisor fixtures only."""
import sys
from pathlib import Path
import xml.etree.ElementTree as ET

args = sys.argv[1:]
results = Path(args[args.index('--results-directory') + 1])
logger = next(v for v in args if v.startswith('trx;LogFileName='))
results.mkdir(parents=True, exist_ok=True)
root = ET.Element('TestRun')
ET.SubElement(ET.SubElement(root, 'Results'), 'UnitTestResult', testName='Synthetic supervisor control', outcome='Passed')
ET.SubElement(ET.SubElement(root, 'ResultSummary'), 'Counters', total='1', executed='1', passed='1', failed='0')
ET.ElementTree(root).write(results / logger.split('=', 1)[1])
