import * as reactiveUtils from "@arcgis/core/core/reactiveUtils";

let ready = false;

const watchHandle = reactiveUtils.watch(
  () => ready,
  () => {
    ready = true;
  },
  { initial: true },
);

reactiveUtils.when(
  () => ready,
  () => {
    ready = true;
  },
  { initial: true },
);

reactiveUtils.whenOnce(() => ready);
watchHandle.remove();
