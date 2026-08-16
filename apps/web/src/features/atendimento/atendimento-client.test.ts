import { afterEach, describe, expect, it, vi } from "vitest";
import { type AtendimentoCard, atendimentoClient } from "./atendimento-client";

const antiforgeryPath = "/foundation/identity/browser-session/antiforgery";

const createdCard: AtendimentoCard = {
  assigneeMembershipId: null,
  column: "novo",
  companyId: "11111111-1111-4111-8111-111111111111",
  description: "Pedido de ajuda",
  id: "22222222-2222-4222-8222-222222222222",
  title: "Ajuda com cobrança",
  version: 1,
};

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
        cache: "no-store",
        credentials: "same-origin",
        mode: "same-origin",
        redirect: "error",
      }),
    );
  });

  it("gets a BFF antiforgery token before creating a card and sends it only with the mutation", async () => {
    const fetchSpy = vi
      .fn()
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ requestToken: "csrf-token" }), {
          status: 200,
        }),
      )
      .mockResolvedValueOnce(
        new Response(JSON.stringify(createdCard), { status: 201 }),
      );
    vi.stubGlobal("fetch", fetchSpy);

    await expect(
      atendimentoClient.create(
        createdCard.companyId,
        createdCard.title,
        createdCard.description,
      ),
    ).resolves.toEqual({ card: createdCard, ok: true });

    expect(fetchSpy.mock.calls).toHaveLength(2);
    expect(fetchSpy.mock.calls[0]).toEqual([
      antiforgeryPath,
      expect.objectContaining({
        cache: "no-store",
        credentials: "same-origin",
        headers: { Accept: "application/json" },
        method: "GET",
        mode: "same-origin",
        redirect: "error",
      }),
    ]);
    expect(fetchSpy.mock.calls[1]).toEqual([
      "/atendimento/cards",
      expect.objectContaining({
        cache: "no-store",
        credentials: "same-origin",
        headers: {
          "Content-Type": "application/json",
          "X-CSRF-TOKEN": "csrf-token",
        },
        method: "POST",
        mode: "same-origin",
        redirect: "error",
      }),
    ]);
    expect(JSON.parse(String(fetchSpy.mock.calls[1]?.[1]?.body))).toMatchObject(
      {
        companyId: createdCard.companyId,
        description: createdCard.description,
        title: createdCard.title,
      },
    );
  });

  it("does not post a card mutation when antiforgery cannot be acquired", async () => {
    const fetchSpy = vi
      .fn()
      .mockResolvedValue(new Response(null, { status: 503 }));
    vi.stubGlobal("fetch", fetchSpy);

    await expect(
      atendimentoClient.create(
        createdCard.companyId,
        createdCard.title,
        createdCard.description,
      ),
    ).resolves.toEqual({ code: "atendimento_unavailable", ok: false });

    expect(fetchSpy.mock.calls).toHaveLength(1);
    expect(fetchSpy.mock.calls[0]?.[0]).toBe(antiforgeryPath);
  });
});
