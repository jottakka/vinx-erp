import { afterEach, describe, expect, it, vi } from "vitest";
import type { DemandBoardCard } from "./demandas-client";
import { demandasClient } from "./demandas-client";

const antiforgeryPath = "/foundation/identity/browser-session/antiforgery";
const companyId = "11111111-1111-4111-8111-111111111111";
const demandId = "22222222-2222-4222-8222-222222222222";
const requesterId = "33333333-3333-4333-8333-333333333333";

const card: DemandBoardCard = {
  demandDisplayNumber: "DEM-000001",
  demandNumber: 1,
  effectiveDeadline: null,
  id: demandId,
  isOverdue: false,
  automationPaused: false,
  priority: "high" as const,
  responsibleMembershipId: companyId,
  stage: "initiation" as const,
  stageStartedAt: "2026-08-26T12:00:00Z",
  title: "Pagar fornecedor",
  version: 1,
};

// Contrato real de comando: DemandResponse completa.
const commandDemand = {
  automationPaused: false,
  companyId,
  cycleCount: 1,
  departamento: null,
  demandDisplayNumber: "DEM-000001",
  demandNumber: 1,
  description: "Pagar fornecedor",
  effectiveDeadline: null,
  id: demandId,
  isOverdue: false,
  manualDueAt: null,
  priority: "high" as const,
  responsibleMembershipId: companyId,
  routineId: null,
  slaDueAt: null,
  stage: "initiation" as const,
  stageDueAt: null,
  title: "Pagar fornecedor",
  tipo: null,
  version: 2,
};

function antiforgeryAware(response: () => Response) {
  return vi.fn(async (input: RequestInfo | URL) => {
    if (String(input) === antiforgeryPath)
      return new Response(JSON.stringify({ requestToken: "tok" }), {
        status: 200,
      });
    return response();
  });
}

afterEach(() => vi.unstubAllGlobals());

describe("demandas client", () => {
  it("loads the board with server-side filters through the same-origin API", async () => {
    const fetchSpy = vi.fn().mockResolvedValue(
      new Response(
        JSON.stringify({
          hasMore: false,
          items: [card],
          nextCursor: null,
          total: 1,
        }),
        { status: 200 },
      ),
    );
    vi.stubGlobal("fetch", fetchSpy);

    const page = await demandasClient.board(companyId, {
      priority: "high" as const,
      search: "fornecedor",
      responsibleMembershipId: requesterId,
    });

    expect(page?.items).toHaveLength(1);
    expect(page?.items[0]?.id).toBe(demandId);
    const [calledPath] = fetchSpy.mock.calls[0] as [string, RequestInit];
    const url = new URL(calledPath, "https://local");
    expect(url.pathname).toBe("/demandas/board");
    expect(url.searchParams.get("companyId")).toBe(companyId);
    expect(url.searchParams.get("priority")).toBe("high");
    expect(url.searchParams.get("search")).toBe("fornecedor");
    expect(url.searchParams.get("responsibleMembershipId")).toBe(requesterId);
    expect(fetchSpy).toHaveBeenCalledWith(
      expect.anything(),
      expect.objectContaining({
        cache: "no-store",
        credentials: "same-origin",
        mode: "same-origin",
      }),
    );
  });

  it("gets a BFF antiforgery token before advancing a stage", async () => {
    let calls = 0;
    const fetchSpy = antiforgeryAware(() => {
      calls += 1;
      return new Response(JSON.stringify(commandDemand), {
        status: 200,
      });
    });
    vi.stubGlobal("fetch", fetchSpy);

    const result = await demandasClient.advance(companyId, card);

    expect(calls).toBe(1);
    expect(result.ok).toBe(true);
    if (result.ok) expect(result.demand?.version).toBe(2);
    const [, init] = fetchSpy.mock.calls[1] as unknown as [
      string,
      Record<string, unknown>,
    ];
    expect(init.method).toBe("POST");
    expect(init.headers).toMatchObject({ "X-CSRF-TOKEN": "tok" });
    const body = JSON.parse(String(init.body)) as Record<string, unknown>;
    expect(body.expectedVersion).toBe(1);
    expect(typeof body.requestId).toBe("string");
  });

  it("rejects the legacy command envelope as a malformed 2xx payload", async () => {
    const fetchSpy = antiforgeryAware(
      () =>
        new Response(JSON.stringify({ demand: commandDemand }), {
          status: 200,
        }),
    );
    vi.stubGlobal("fetch", fetchSpy);

    const result = await demandasClient.advance(companyId, card);

    expect(result).toEqual({
      code: "demandas_unavailable",
      ok: false,
    });
  });

  it("surfaces integrity conflicts with the current version for recovery", async () => {
    const fetchSpy = antiforgeryAware(
      () =>
        new Response(
          JSON.stringify({
            code: "integrity_conflict",
            current: { stage: "execution", version: 7 },
          }),
          { status: 409 },
        ),
    );
    vi.stubGlobal("fetch", fetchSpy);

    const result = await demandasClient.advance(companyId, card);

    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.current?.version).toBe(7);
  });

  it("rejects a malformed board payload as a protocol failure", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        new Response(
          JSON.stringify([
            // Campo obrigatório ausente (stageStartedAt) — contrato estrito.
            { ...card },
          ]),
          { status: 200 },
        ),
      ),
    );

    const page = await demandasClient.board(companyId);

    expect(page).toBeNull();
  });

  it("rejects a board payload with an unexpected extra property", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        new Response(
          JSON.stringify({
            hasMore: false,
            items: [{ ...card, injected: true }],
            nextCursor: null,
            total: 1,
          }),
          { status: 200 },
        ),
      ),
    );

    const page = await demandasClient.board(companyId);

    expect(page).toBeNull();
  });

  it("rejects a malformed detail payload", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        new Response(JSON.stringify({ demand: commandDemand }), {
          status: 200,
        }),
      ),
    );

    const detail = await demandasClient.detail(companyId, demandId);

    expect(detail).toBeNull();
  });

  it("rejects a malformed command payload without exposing raw data", async () => {
    const fetchSpy = antiforgeryAware(
      () =>
        new Response(JSON.stringify({ ...commandDemand, version: "1" }), {
          status: 200,
        }),
    );
    vi.stubGlobal("fetch", fetchSpy);

    const result = await demandasClient.advance(companyId, card);

    expect(result).toEqual({
      code: "demandas_unavailable",
      ok: false,
    });
  });
});
