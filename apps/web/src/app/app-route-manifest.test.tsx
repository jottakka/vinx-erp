import { describe, expect, it } from "vitest";
import { PeoplePage } from "../features/people/people-page";
import { APP_PATHS } from "../shared/routing/app-paths";
import { appRouteManifest } from "./app-route-manifest";
import { FeaturePreparationPage } from "./feature-preparation-page";

describe("app route manifest", () => {
  it("attaches app presentation to every and only safe catalog path", () => {
    expect(appRouteManifest.map((route) => route.path)).toEqual(APP_PATHS);
  });

  it("wires Pessoas to the employee directory without changing deferred modules", () => {
    const byPath = new Map(
      appRouteManifest.map((route) => [route.path, route]),
    );

    expect(byPath.get("/pessoas")?.element.type).toBe(PeoplePage);
    expect(byPath.get("/paguai")?.element.type).toBe(FeaturePreparationPage);
    expect(byPath.get("/atendimento")?.element.type).toBe(
      FeaturePreparationPage,
    );
    expect(byPath.get("/kanban")?.element.type).toBe(FeaturePreparationPage);
  });
});
