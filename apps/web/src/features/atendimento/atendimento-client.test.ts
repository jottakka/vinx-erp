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
  categoryRefId: null,
  channelRefId: null,
  requesterPersonRef: null,
  watcherMembershipIds: null,
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

  it("sends companyId and queue filters in one query string", async () => {
    const fetchSpy = vi
      .fn()
      .mockResolvedValue(new Response(JSON.stringify([]), { status: 200 }));
    vi.stubGlobal("fetch", fetchSpy);

    await atendimentoClient.list(createdCard.companyId, {
      assigneeMembershipId: "44444444-4444-4444-8444-444444444444",
      overdue: true,
      priority: "alta",
      search: "cobranca",
      status: "novo",
    });

    expect(fetchSpy.mock.calls[0]?.[0]).toBe(
      "/atendimento/cards?companyId=11111111-1111-4111-8111-111111111111&assigneeMembershipId=44444444-4444-4444-8444-444444444444&overdue=true&priority=alta&search=cobranca&status=novo",
    );
  });

  it("parses a queue card with null priority", async () => {
    const card = { ...createdCard, priority: null };
    const fetchSpy = vi
      .fn()
      .mockResolvedValue(new Response(JSON.stringify([card]), { status: 200 }));
    vi.stubGlobal("fetch", fetchSpy);

    await expect(
      atendimentoClient.list(createdCard.companyId),
    ).resolves.toEqual([card]);
  });

  it("accepts a successful fields response with null priority", async () => {
    const updated = { ...createdCard, priority: null, version: 2 };
    const fetchSpy = csrfThenOk(updated);
    vi.stubGlobal("fetch", fetchSpy);

    await expect(
      atendimentoClient.fields(createdCard.companyId, createdCard, {
        priority: null,
      }),
    ).resolves.toEqual({ card: updated, ok: true });
  });

  it("keeps a 409 current snapshot with null priority", async () => {
    const current = { ...createdCard, priority: null, version: 2 };
    const fetchSpy = vi
      .fn()
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ requestToken: "csrf-token" }), {
          status: 200,
        }),
      )
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ code: "integrity_conflict", current }), {
          status: 409,
        }),
      );
    vi.stubGlobal("fetch", fetchSpy);

    await expect(
      atendimentoClient.move(
        createdCard.companyId,
        createdCard,
        "em_andamento",
      ),
    ).resolves.toEqual({
      code: "integrity_conflict",
      current,
      ok: false,
    });
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

  it("loads validated directory options for the company", async () => {
    const option = {
      displayName: "Maria Silva",
      employeeId: "55555555-5555-4555-8555-555555555555",
      membershipId: "33333333-3333-4333-8333-333333333333",
    };
    const fetchSpy = vi
      .fn()
      .mockResolvedValue(
        new Response(JSON.stringify([option]), { status: 200 }),
      );
    vi.stubGlobal("fetch", fetchSpy);

    await expect(
      atendimentoClient.directoryOptions(createdCard.companyId),
    ).resolves.toEqual([option]);
  });

  it("returns null for malformed directory options", async () => {
    await Promise.all(
      [
        // membership-id não canônico não vira rótulo de pessoa.
        [
          {
            displayName: "Maria Silva",
            employeeId: "55555555-5555-4555-8555-555555555555",
            membershipId: "qualquer-coisa",
          },
        ],
        [
          {
            displayName: "",
            employeeId: "55555555-5555-4555-8555-555555555555",
            membershipId: "33333333-3333-4333-8333-333333333333",
          },
        ],
        [
          {
            displayName: "Maria Silva",
            employeeId: "55555555-5555-4555-8555-555555555555",
            membershipId: "33333333-3333-4333-8333-333333333333",
            extra: "campo desconhecido",
          },
        ],
      ].map(async (payload) => {
        const fetchSpy = vi
          .fn()
          .mockResolvedValue(
            new Response(JSON.stringify(payload), { status: 200 }),
          );
        vi.stubGlobal("fetch", fetchSpy);

        await expect(
          atendimentoClient.directoryOptions(createdCard.companyId),
        ).resolves.toBeNull();
      }),
    );
  });

  it("creates a category through the antiforgery + ambiguous POST flow", async () => {
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
            companyId: createdCard.companyId,
            id: "44444444-4444-4444-8444-444444444444",
            name: "Suporte",
          }),
          { status: 201 },
        ),
      );
    vi.stubGlobal("fetch", fetchSpy);

    await expect(
      atendimentoClient.createCategory(createdCard.companyId, "  Suporte  "),
    ).resolves.toEqual({
      category: {
        companyId: createdCard.companyId,
        id: "44444444-4444-4444-8444-444444444444",
        name: "Suporte",
      },
      ok: true,
    });

    expect(fetchSpy.mock.calls[1]?.[0]).toBe("/atendimento/categories");
    expect(JSON.parse(String(fetchSpy.mock.calls[1]?.[1]?.body))).toMatchObject(
      {
        companyId: createdCard.companyId,
        name: "Suporte",
        requestId: expect.stringMatching(
          /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i,
        ),
      },
    );
  });

  it("marks an ambiguous category create as possibly committed on transport failure", async () => {
    const fetchSpy = vi
      .fn()
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ requestToken: "csrf-token" }), {
          status: 200,
        }),
      )
      .mockRejectedValueOnce(new TypeError("network error"));
    vi.stubGlobal("fetch", fetchSpy);

    await expect(
      atendimentoClient.createCategory(createdCard.companyId, "Suporte"),
    ).resolves.toEqual({
      code: "atendimento_unavailable",
      mayHaveCommitted: true,
      ok: false,
    });
  });

  it("marks a category create with an invalid 2xx body as possibly committed", async () => {
    const fetchSpy = vi
      .fn()
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ requestToken: "csrf-token" }), {
          status: 200,
        }),
      )
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ id: "not-a-uuid", name: "Suporte" }), {
          status: 201,
        }),
      );
    vi.stubGlobal("fetch", fetchSpy);

    await expect(
      atendimentoClient.deactivateCategory(
        createdCard.companyId,
        "44444444-4444-4444-8444-444444444444",
      ),
    ).resolves.toEqual({
      code: "atendimento_unavailable",
      mayHaveCommitted: true,
      ok: false,
    });
    expect(fetchSpy.mock.calls[1]?.[0]).toBe(
      "/atendimento/categories/44444444-4444-4444-8444-444444444444/deactivate",
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

  it("marks a malformed 2xx command response as possibly committed", async () => {
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
    ).resolves.toEqual({
      code: "atendimento_unavailable",
      mayHaveCommitted: true,
      ok: false,
    });
  });

  it("marks a transport failure after POST as possibly committed", async () => {
    const fetchSpy = vi
      .fn()
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ requestToken: "csrf-token" }), {
          status: 200,
        }),
      )
      .mockRejectedValueOnce(new TypeError("network error"));
    vi.stubGlobal("fetch", fetchSpy);

    await expect(
      atendimentoClient.move(
        createdCard.companyId,
        createdCard,
        "em_andamento",
      ),
    ).resolves.toEqual({
      code: "atendimento_unavailable",
      mayHaveCommitted: true,
      ok: false,
    });
  });

  it.each([
    ["unknown response key", { ...createdCard, unexpected: true }],
    ["malformed card ID", { ...createdCard, id: "not-an-id" }],
    ["malformed company ID", { ...createdCard, companyId: "not-an-id" }],
    [
      "nil assignee ID",
      {
        ...createdCard,
        assigneeMembershipId: "00000000-0000-0000-0000-000000000000",
      },
    ],
    [
      "nil attachment ID",
      {
        ...createdCard,
        attachmentFileId: "00000000-0000-0000-0000-000000000000",
      },
    ],
    [
      "nil participant ID",
      {
        ...createdCard,
        participantMembershipIds: ["00000000-0000-0000-0000-000000000000"],
      },
    ],
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

function csrfThenOk(card: AtendimentoCard) {
  // Cada comando busca um token novo e um corpo 2xx próprio: respostas são
  // descartáveis (corpo consumido uma única vez), então alternamos sempre
  // instâncias frescas de token → cartão.
  let call = 0;
  return vi.fn().mockImplementation(() => {
    const isTokenRequest = call++ % 2 === 0;
    return Promise.resolve(
      isTokenRequest
        ? new Response(JSON.stringify({ requestToken: "csrf-token" }), {
            status: 200,
          })
        : new Response(JSON.stringify(card), { status: 200 }),
    );
  });
}
