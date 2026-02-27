import { describe, expect, it } from "vitest";

import { CompatEventBus, TimeSliderCompat } from "../src/index.js";

describe("TimeSliderCompat", () => {
  it("steps through configured stop values and emits update events", () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const slider = new TimeSliderCompat({
      eventBus,
      stops: {
        values: ["2024-01-01T00:00:00.000Z", "2024-02-01T00:00:00.000Z"],
      },
    });

    slider.next();
    expect(slider.timeExtent?.start.toISOString()).toBe("2024-02-01T00:00:00.000Z");

    slider.previous();
    expect(slider.timeExtent?.start.toISOString()).toBe("2024-01-01T00:00:00.000Z");
    expect(seenTypes).toContain("timeslider.updated");
    expect(seenTypes).toContain("timeslider.next");
    expect(seenTypes).toContain("timeslider.previous");
  });

  it("supports play/stop lifecycle", () => {
    const slider = new TimeSliderCompat({
      timeExtent: {
        start: "2024-01-01T00:00:00.000Z",
        end: "2024-01-02T00:00:00.000Z",
      },
      stops: {
        interval: {
          value: 1,
          unit: "days",
        },
      },
    });

    slider.play();
    expect(slider.playing).toBe(true);
    slider.next();
    expect(slider.timeExtent?.start.toISOString()).toBe("2024-01-02T00:00:00.000Z");
    slider.stop();
    expect(slider.playing).toBe(false);
  });
});
