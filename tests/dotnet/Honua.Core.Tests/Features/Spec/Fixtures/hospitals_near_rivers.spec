grammar "v1.0"
kind    "analysis"
title   "hospitals within 500 m of rivers"

# Canonical reference fixture used by the round-trip and end-to-end tests.
source hospitals {
  type = "layer"
  ref  = "osm:amenity=hospital"
}

source rivers {
  type = "layer"
  ref  = "osm:waterway=river"
}

scope {
  target = @hospitals
  where  = cql2("state = 'CA'")
}

compute river_buffer {
  op     = buffer
  inputs = { input = @rivers }
  params = { distance = 500.m, crs = "EPSG:3857" }
}

compute at_risk {
  op     = spatial_join
  inputs = { left = @hospitals, right = @river_buffer }
  params = { crs = "EPSG:3857" }
}

output at_risk_features {
  expr = @at_risk
}
