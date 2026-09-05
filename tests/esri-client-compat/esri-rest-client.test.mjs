import assert from 'node:assert/strict';
import { test } from 'node:test';
import { queryFeatures } from '@esri/arcgis-rest-feature-service';
import { geocode } from '@esri/arcgis-rest-geocoding';
import { request } from '@esri/arcgis-rest-request';

const base = (process.env.HONUA_ESRI_PROBE_URL ?? 'http://127.0.0.1:5555').replace(/\/$/, '');

test('@esri/arcgis-rest-feature-service query returns seeded features', async () => {
  const result = await queryFeatures({
    url: `${base}/rest/services/admin_sample/FeatureServer/3000`,
    where: '1=1',
    outFields: '*',
    returnGeometry: false,
    resultRecordCount: 100,
  });

  assert.equal(result.features.length, 4);
});

test('@esri/arcgis-rest-request discovers token auth from /rest/info', async () => {
  const info = await request(`${base}/rest/info`, {
    httpMethod: 'GET',
    params: { f: 'json' },
  });

  assert.equal(info.authInfo?.isTokenBasedSecurity, true);
  assert.match(info.authInfo?.tokenServicesUrl ?? '', /\/sharing\/rest\/generateToken$/);
});

test('@esri/arcgis-rest-geocoding POSTs findAddressCandidates', async () => {
  const result = await geocode({
    endpoint: `${base}/rest/services/GeocodeServer`,
    singleLine: 'Honolulu',
  });

  assert.ok(Array.isArray(result.candidates));
});
