export interface MigrationDemoFixtureTarget {
  fixtureName: string;
  sampleTitle: string;
  sourceUrl: string;
  fixturePin: string;
  capturedAt: string;
}

export const MIGRATION_DEMO_ISSUE_NUMBER = 327;

export const MIGRATION_DEMO_PRIMARY_TARGET: MigrationDemoFixtureTarget = {
  fixtureName: "esri-demo-feature-table-relates-app",
  sampleTitle: "FeatureTable with related records",
  sourceUrl: "https://developers.arcgis.com/javascript/latest/sample-code/widgets-featuretable-relates/",
  fixturePin: "featuretable-relates-v2026-03-02",
  capturedAt: "2026-03-02",
};

export const MIGRATION_DEMO_FALLBACK_TARGET: MigrationDemoFixtureTarget = {
  fixtureName: "esri-demo-feature-table-popup-interaction-app",
  sampleTitle: "Feature table with popup interaction",
  sourceUrl: "https://developers.arcgis.com/javascript/latest/sample-code/widgets-featuretable-popup-interaction/",
  fixturePin: "featuretable-popup-interaction-v2026-03-02",
  capturedAt: "2026-03-02",
};

export const MIGRATION_DEMO_TARGETS: readonly MigrationDemoFixtureTarget[] = [
  MIGRATION_DEMO_PRIMARY_TARGET,
  MIGRATION_DEMO_FALLBACK_TARGET,
];
