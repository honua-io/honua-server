import RouteTask from "@arcgis/core/rest/route/RouteTask";

const routeTask = new RouteTask({
  url: "https://example.test/rest/services/network/RouteServer",
});

const result = await routeTask.solve({
  stops: {
    features: [
      { geometry: { x: -157.8583, y: 21.3069 }, attributes: { Name: "Start" } },
      { geometry: { x: -157.9076, y: 21.3035 }, attributes: { Name: "End" } },
    ],
  },
  returnDirections: true,
});

void result;
