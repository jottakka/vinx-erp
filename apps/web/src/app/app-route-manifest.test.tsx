import { describe, expect, it } from "vitest";
import { APP_PATHS } from "../shared/routing/app-paths";
import { appRouteManifest } from "./app-route-manifest";

describe("app route manifest", () => {
  it("attaches app presentation to every and only safe catalog path", () => {
    expect(appRouteManifest.map((route) => route.path)).toEqual(APP_PATHS);
  });
});
