/**
 * Polyfill DOMParser and XMLSerializer for Node.js.
 *
 * OpenLayers WFS/GML format parsers require these browser APIs.
 * Import this module as a side-effect in tests that use ol/format/WFS.
 */

import { JSDOM } from 'jsdom';

if (typeof globalThis.DOMParser === 'undefined') {
  const dom = new JSDOM('');
  globalThis.DOMParser = dom.window.DOMParser;
  globalThis.XMLSerializer = dom.window.XMLSerializer;
  globalThis.Document = dom.window.Document;
  globalThis.Node = dom.window.Node;
}
