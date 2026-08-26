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

  it("sends only the provided ticketing fields and keeps null-means-unchanged semantics", async () => {
    const fetchSpy = csrfThenOk(createdCard);
    vi.stubGlobal("fetch", fetchSpy);

    await atendimentoClient.fields(createdCard.companyId, createdCard, {
      priority: "alta",
      slaDueAt: "2026-09-01T12:00:00.000Z",
    });

    const body = JSON.parse(String(fetchSpy.mock.calls[1]?.[1]?.body));
    expect(body).toMatchObject({
      companyId: createdCard.companyId,
      expectedVersion: createdCard.version,
      priority: "alta",
      slaDueAt: "2026-09-01T12:00:00.000Z",
    });
    // Campos ausentes não são enviados (o servidor os mantém).
    expect(body).not.toHaveProperty("categoryRefId");
    expect(body).not.toHaveProperty("requesterPersonRef");
    expect(fetchSpy.mock.calls[1]?.[0]).toBe(
      `/atendimento/cards/${createdCard.id}/fields`,
    );
  });

  it("posts watcher add and remove to dedicated routes", async () => {
    const membershipId = "33333333-3333-4333-8333-333333333333";
    const fetchSpy = csrfThenOk(createdCard);
    vi.stubGlobal("fetch", fetchSpy);

    await atendimentoClient.addWatcher(
      createdCard.companyId,
      createdCard,
      membershipId,
    );
    await atendimentoClient.removeWatcher(
      createdCard.companyId,
      createdCard,
      membershipId,
    );

    expect(fetchSpy.mock.calls[1]?.[0]).toBe(
      `/atendimento/cards/${createdCard.id}/watchers/add`,
    );
    expect(fetchSpy.mock.calls[3]?.[0]).toBe(
      `/atendimento/cards/${createdCard.id}/watchers/remove`,
    );
    expect(JSON.parse(String(fetchSpy.mock.calls[1]?.[1]?.body))).toMatchObject(
      {
        memberMembershipId: membershipId,
      },
    );
  });

  it("reads timeline and lookups through company-scoped GETs", async () => {
    const fetchSpy = vi
      .fn()
      .mockResolvedValue(new Response(JSON.stringify([]), { status: 200 }));
    vi.stubGlobal("fetch", fetchSpy);

    await atendimentoClient.events(createdCard.id, createdCard.companyId);
    await atendimentoClient.lookups(createdCard.companyId);
    await atendimentoClient.directoryOptions(createdCard.companyId);

    expect(fetchSpy.mock.calls.map(([path]) => path)).toEqual([
      `/atendimento/cards/${createdCard.id}/events?companyId=${createdCard.companyId}`,
      `/atendimento/lookups?companyId=${createdCard.companyId}`,
      `/foundation/companies/${createdCard.companyId}/directory-options`,
    ]);
  });
});

function csrfThenOk(card: AtendimentoCard) {
  return vi
    .fn()
    .mockResolvedValueOnce(
      new Response(JSON.stringify({ requestToken: "csrf-token" }), {
        status: 200,
      }),
    )
    .mockResolvedValue(new Response(JSON.stringify(card), { status: 200 }));
}
