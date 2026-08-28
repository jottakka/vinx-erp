import { describe, expect, it } from "vitest";
import { AtendimentoPage } from "../features/atendimento/atendimento-page";
import { PaguaiPage } from "../features/paguai/paguai-page";
import { PeoplePage } from "../features/people/people-page";
import { APP_PATHS } from "../shared/routing/app-paths";
import { appRouteManifest } from "./app-route-manifest";

describe("app route manifest", () => {
  it("attaches app presentation to every and only safe catalog path", () => {
    expect(appRouteManifest.map((route) => route.path)).toEqual(APP_PATHS);
  });

  it("wires the live module workspaces and employee directory", () => {
    const byPath = new Map(
      appRouteManifest.map((route) => [route.path, route]),
    );

    expect(byPath.get("/pessoas")?.element.type).toBe(PeoplePage);
    expect(byPath.get("/paguai")?.element.type).toBe(PaguaiPage);
    expect(byPath.get("/atendimento")?.element.type).toBe(AtendimentoPage);
    expect(byPath.get("/kanban")?.element.type).toBe(AtendimentoPage);
  });
});
