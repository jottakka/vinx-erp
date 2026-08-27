import { afterEach, describe, expect, it, vi } from "vitest";
import {
  type AtendimentoCard,
  type AtendimentoCreateOptions,
  atendimentoClient,
} from "./atendimento-client";

const antiforgeryPath = "/foundation/identity/browser-session/antiforgery";
const requestId = "99999999-9999-4999-8999-999999999999";

function createWithRequestId(
  companyId: string,
  stableRequestId: string,
  title: string,
  description: string,
  options?: AtendimentoCreateOptions,
) {
  return (
    atendimentoClient.create as unknown as (
      companyId: string,
      requestId: string,
      title: string,
      description: string,
      options?: AtendimentoCreateOptions,
    ) => ReturnType<typeof atendimentoClient.create>
  )(companyId, stableRequestId, title, description, options);
}

const createdCard: AtendimentoCard = {
  attachmentFileId: null,
  assigneeMembershipId: null,
  column: "novo",
  companyId: "11111111-1111-4111-8111-111111111111",
  description: "Pedido de ajuda",
  id: "22222222-2222-4222-8222-222222222222",
  participantMembershipIds: [],
  priority: "media",
  channel: "manual",
  slaDueAt: null,
  isOverdue: false,
  createdAt: "2026-08-26T12:00:00.000Z",
  updatedAt: "2026-08-26T12:00:00.000Z",
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
      createWithRequestId(
        createdCard.companyId,
        requestId,
        createdCard.title,
        createdCard.description,
        {
          attachmentFileId: "33333333-3333-4333-8333-333333333333",
          participantMembershipIds: ["44444444-4444-4444-8444-444444444444"],
          priority: "alta",
          channel: "web",
          slaDueAt: "2026-08-28T18:00:00.000Z",
        },
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
        attachmentFileId: "33333333-3333-4333-8333-333333333333",
        participantMembershipIds: ["44444444-4444-4444-8444-444444444444"],
        priority: "alta",
        channel: "web",
        slaDueAt: "2026-08-28T18:00:00.000Z",
        requestId,
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
      createWithRequestId(
        createdCard.companyId,
        requestId,
        createdCard.title,
        createdCard.description,
      ),
    ).resolves.toEqual({ code: "atendimento_unavailable", ok: false });

    expect(fetchSpy.mock.calls).toHaveLength(1);
    expect(fetchSpy.mock.calls[0]?.[0]).toBe(antiforgeryPath);
  });

  it("uses web and medium defaults in the direct create request", async () => {
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

    await createWithRequestId(
      createdCard.companyId,
      requestId,
      createdCard.title,
      createdCard.description,
    );

    expect(JSON.parse(String(fetchSpy.mock.calls[1]?.[1]?.body))).toMatchObject(
      {
        channel: "web",
        priority: "media",
        slaDueAt: null,
      },
    );
  });

  it("loads named assignment options only for the explicit company", async () => {
    const fetchSpy = vi.fn().mockResolvedValue(
      new Response(
        JSON.stringify([
          {
            displayName: "Maria Silva",
            membershipId: "33333333-3333-4333-8333-333333333333",
          },
        ]),
        { status: 200 },
      ),
    );
    vi.stubGlobal("fetch", fetchSpy);

    await expect(
      atendimentoClient.assignmentOptions(createdCard.companyId),
    ).resolves.toEqual([
      {
        displayName: "Maria Silva",
        membershipId: "33333333-3333-4333-8333-333333333333",
      },
    ]);

    expect(fetchSpy).toHaveBeenCalledWith(
      "/atendimento/assignment-options?companyId=11111111-1111-4111-8111-111111111111",
      expect.objectContaining({ credentials: "same-origin" }),
    );
  });

  it("returns null for malformed assignment options", async () => {
    await Promise.all(
      [
        [{ displayName: "Maria Silva", membershipId: "not-a-uuid" }],
        [
          {
            displayName: "Maria Silva",
            membershipId: "00000000-0000-0000-0000-000000000000",
          },
        ],
        [
          {
            displayName: "",
            membershipId: "33333333-3333-4333-8333-333333333333",
          },
        ],
        [
          {
            displayName: "Maria Silva",
            extra: "campo desconhecido",
            membershipId: "33333333-3333-4333-8333-333333333333",
          },
        ],
        [
          {
            displayName: "Maria Silva",
            membershipId: "33333333-3333-4333-8333-333333333333",
          },
          { displayName: "Inválido", membershipId: "também-não" },
        ],
      ].map(async (payload) => {
        const fetchSpy = vi
          .fn()
          .mockResolvedValue(
            new Response(JSON.stringify(payload), { status: 200 }),
          );
        vi.stubGlobal("fetch", fetchSpy);

        await expect(
          atendimentoClient.assignmentOptions(createdCard.companyId),
        ).resolves.toBeNull();
      }),
    );
  });

  it.each([
    ["unknown priority", { ...createdCard, priority: "urgent" }],
    ["unknown channel", { ...createdCard, channel: "phone" }],
    ["malformed SLA timestamp", { ...createdCard, slaDueAt: "tomorrow" }],
    ["malformed created timestamp", { ...createdCard, createdAt: "yesterday" }],
    ["malformed updated timestamp", { ...createdCard, updatedAt: "yesterday" }],
  ] as const)("rejects a response with %s", async (_label, card) => {
    const fetchSpy = vi
      .fn()
      .mockResolvedValue(new Response(JSON.stringify([card]), { status: 200 }));
    vi.stubGlobal("fetch", fetchSpy);

    await expect(atendimentoClient.list(createdCard.companyId)).resolves.toBe(
      null,
    );
  });

  it("rejects a malformed successful command response", async () => {
    const fetchSpy = vi
      .fn()
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ requestToken: "csrf-token" }), {
          status: 200,
        }),
      )
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ ...createdCard, priority: "urgent" }), {
          status: 200,
        }),
      );
    vi.stubGlobal("fetch", fetchSpy);

    await expect(
      atendimentoClient.move(
        createdCard.companyId,
        createdCard,
        "em_andamento",
      ),
    ).resolves.toEqual({ code: "atendimento_unavailable", ok: false });
  });

  it.each([
    ["unknown response key", { ...createdCard, unexpected: true }],
    ["malformed card ID", { ...createdCard, id: "not-an-id" }],
    ["malformed company ID", { ...createdCard, companyId: "not-an-id" }],
    ["fractional version", { ...createdCard, version: 1.5 }],
    ["non-positive version", { ...createdCard, version: 0 }],
  ] as const)("rejects a card with %s", async (_label, card) => {
    const fetchSpy = vi
      .fn()
      .mockResolvedValue(new Response(JSON.stringify([card]), { status: 200 }));
    vi.stubGlobal("fetch", fetchSpy);

    await expect(atendimentoClient.list(createdCard.companyId)).resolves.toBe(
      null,
    );
  });

  it("omits a malformed current card from a conflict response", async () => {
    const fetchSpy = vi
      .fn()
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ requestToken: "csrf-token" }), {
          status: 200,
        }),
      )
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            code: "integrity_conflict",
            current: { ...createdCard, updatedAt: "not-a-timestamp" },
          }),
          { status: 409 },
        ),
      );
    vi.stubGlobal("fetch", fetchSpy);

    await expect(
      atendimentoClient.move(
        createdCard.companyId,
        createdCard,
        "em_andamento",
      ),
    ).resolves.toEqual({ code: "integrity_conflict", ok: false });
  });

  it("cancels with the current version, request ID, and trimmed reason", async () => {
    const fetchSpy = vi
      .fn()
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ requestToken: "csrf-token" }), {
          status: 200,
        }),
      )
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ ...createdCard, column: "cancelado" }), {
          status: 200,
        }),
      );
    vi.stubGlobal("fetch", fetchSpy);

    await atendimentoClient.cancel(
      createdCard.companyId,
      createdCard,
      "  Duplicado  ",
    );

    expect(fetchSpy.mock.calls[1]?.[0]).toBe(
      `/atendimento/cards/${createdCard.id}/cancel`,
    );
    expect(JSON.parse(String(fetchSpy.mock.calls[1]?.[1]?.body))).toMatchObject(
      {
        companyId: createdCard.companyId,
        expectedVersion: createdCard.version,
        reason: "Duplicado",
      },
    );
  });
});
