/** Literal, independently encoded Mapbox Vector Tile 2.1 controls.
 * Field layout: Tile.layers=3; Layer.name=1/features=2/keys=3/values=4/extent=5/version=15.
 * Feature.id=1/tags=2/type=3/geometry=4; geometry uses MoveTo(9), LineTo, ClosePath(15).
 * Integer positions below were authored separately from the JS oracle and server.
 * No producer, transform or builder runs in these controls; OpenLayers decodes these fixed bytes.
 */
export const FIXTURE_MVT_HEX: Readonly<Record<string, string>> = {
  // pt-alpha: [(570, 777)]
  // pt-beta: [(636, 712)]
  // pt-gamma: [(496, 860)]
  '2000': '1a680a056c61796572120f0801120200001801220509f408920c120f0802120200011801220509f809900b120f0803120200' +
    '021801220509e007b80d1a046e616d65220a0a0870742d616c70686122090a0770742d62657461220a0a0870742d67616d6d' +
    '612880207802',
  // line-alpha: [(76, 1066), (1009, 477)]
  // line-beta: [(309, 771), (1242, 182)]
  '2001': '1a590a056c6179657212140801120200001802220a099801d4100aca0e990912140802120200011802220a09ea04860c0aca' +
    '0e99091a046e616d65220c0a0a6c696e652d616c706861220b0a096c696e652d626574612880207802',
  // poly-alpha: [(309, 477), (776, 477), (776, 1066), (309, 1066)]
  // poly-beta: [(542, 594), (1009, 594), (1009, 948), (542, 948)]
  '2002': '1a650a056c61796572121a0801120200001803221009ea04ba071aa60700009a09a507000f121a0802120200011803221009' +
    'bc08a4091aa6070000c405a507000f1a046e616d65220c0a0a706f6c792d616c706861220b0a09706f6c792d626574612880' +
    '207802',
};
