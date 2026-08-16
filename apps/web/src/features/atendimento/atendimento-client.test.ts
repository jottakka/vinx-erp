import { afterEach, describe, expect, it, vi } from "vitest";
import { atendimentoClient } from "./atendimento-client";

afterEach(() => vi.unstubAllGlobals());

describe("atendimento client", () => {
  it("loads the explicitly selected company through the same-origin API", async () => {
    const fetchSpy = vi
      .fn()
      .mockResolvedValue(new Response(JSON.stringify([]), { status: 200 }));
    vi.stubGlobal("fetch", fetchSpy);

    await atendimentoClient.list("11111111-1111-4111-8111-111111111111");

    expect(fetchSpy).toHaveBeenCalledWith(
      "/atendimento/cards?companyId=11111111-1111-4111-8111-111111111111",
      expect.objectContaining({
        credentials: "same-origin",
        mode: "same-origin",
        redirect: "error",
      }),
    );
  });
});
