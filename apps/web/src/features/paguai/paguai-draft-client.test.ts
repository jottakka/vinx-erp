import { HttpResponse, http } from "msw";
import { describe, expect, it, vi } from "vitest";
import { server } from "../../test/msw/server";
import { createPaguaiDraftClient } from "./paguai-draft-client";

const companyId = "11111111-1111-4111-8111-111111111111";
const requestId = "22222222-2222-4222-8222-222222222222";
const proofFileId = "44444444-4444-4444-8444-444444444444";
const draft = {
  id: "33333333-3333-4333-8333-333333333333",
  companyId,
  requestId,
  title: "Papel",
  description: "Papel para impressora",
  amount: "1250.00",
  currency: "BRL" as const,
  dueDate: "2026-09-01",
  beneficiaryDisplayName: "Fornecedor Demo",
  invoiceFileId: null,
  proofRequirement: "required" as const,
  state: "rascunho",
  version: 1,
  createdAt: "2026-08-16T12:00:00.000+00:00",
};

const proposalId = "44444444-4444-4444-8444-444444444444";
const proposal = {
  id: proposalId,
  companyId,
  title: "Papel",
  description: "Papel para impressora",
  amount: "1250.00",
  currency: "BRL" as const,
  dueDate: "2026-09-01",
  beneficiaryDisplayName: "Fornecedor Demo",
  proofRequirement: "required" as const,
  invoiceFileId: "55555555-5555-4555-8555-555555555555",
  confidence: 0.98,
  sourceReferences: [{ field: "amount", source: "page:1" }],
  state: "pending" as const,
  createdAt: "2026-08-26T12:00:00.000+00:00",
  expiresAt: "2026-08-26T12:15:00.000+00:00",
};
const antiforgeryPath = "/foundation/identity/browser-session/antiforgery";
const testOrigin = "http://localhost";

function handlerUrl(path: string) {
  return new URL(path, testOrigin).toString();
}

function createFetchSpy() {
  return vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const forwardedInput =
      typeof input === "string" ? new URL(input, testOrigin) : input;
    return globalThis.fetch(forwardedInput, init);
  });
}

describe("Paguai draft client", () => {
  it("reads the exact persisted proposal and confirms it with antiforgery without client-editable financial input", async () => {
    const requests: Request[] = [];
    server.use(
      http.get(
        handlerUrl(`/paguai/draft-proposals/${proposalId}`),
        ({ request }) => {
          expect(new URL(request.url).searchParams.get("companyId")).toBe(
            companyId,
          );
          return HttpResponse.json(proposal);
        },
      ),
      http.get(handlerUrl(antiforgeryPath), () =>
        HttpResponse.json({ requestToken: "csrf" }),
      ),
      http.post(
        handlerUrl(`/paguai/draft-proposals/${proposalId}/confirm`),
        async ({ request }) => {
          requests.push(request);
          return HttpResponse.json(draft, { status: 201 });
        },
      ),
    );
    const fetchSpy = createFetchSpy();
    const client = createPaguaiDraftClient(fetchSpy);

    await expect(client.getProposal(companyId, proposalId)).resolves.toEqual({
      ok: true,
      value: proposal,
    });
    await expect(
      client.confirmProposal(companyId, proposalId, requestId),
    ).resolves.toEqual({
      ok: true,
      value: draft,
    });

    expect(fetchSpy.mock.calls.map(([path]) => String(path))).toEqual([
      `/paguai/draft-proposals/${proposalId}?companyId=${companyId}`,
      antiforgeryPath,
      `/paguai/draft-proposals/${proposalId}/confirm`,
    ]);
    expect(await requests[0]?.json()).toEqual({ companyId, requestId });
    expect(requests[0]?.headers.get("X-CSRF-TOKEN")).toBe("csrf");
  });

  it("uses same-origin antiforgery before creating a strict draft response", async () => {
    const calls: [string, RequestInit][] = [];
    const fetchImplementation: typeof fetch = async (input, init) => {
      calls.push([String(input), init ?? {}]);
      return calls.length === 1
        ? Response.json({ requestToken: "csrf" })
        : Response.json(draft, { status: 201 });
    };

    const result = await createPaguaiDraftClient(fetchImplementation).create({
      companyId,
      requestId,
      title: draft.title,
      description: draft.description,
      amount: draft.amount,
      currency: draft.currency,
      dueDate: draft.dueDate,
      beneficiaryDisplayName: draft.beneficiaryDisplayName,
      invoiceFileId: draft.invoiceFileId,
      proofRequirement: draft.proofRequirement,
    });

    expect(result).toEqual({ ok: true, value: draft });
    expect(calls[0]).toEqual([
      "/foundation/identity/browser-session/antiforgery",
      expect.objectContaining({ credentials: "same-origin", method: "GET" }),
    ]);
    expect(calls[1]).toEqual([
      "/paguai/requests",
      expect.objectContaining({
        credentials: "same-origin",
        headers: expect.objectContaining({ "X-CSRF-TOKEN": "csrf" }),
        method: "POST",
      }),
    ]);
    expect(JSON.parse(String(calls[1][1].body)).amount).toBe("1250.00");
    expect(JSON.parse(String(calls[1][1].body)).invoiceFileId).toBeNull();
  });

  it("requires the nullable invoiceFileId field on strict readback", async () => {
    const fetchImplementation: typeof fetch = async () =>
      Response.json({ ...draft, invoiceFileId: "not-a-uuid" });

    await expect(
      createPaguaiDraftClient(fetchImplementation).detail(companyId, draft.id),
    ).resolves.toEqual({ ok: false, error: "protocol" });
  });

  it("preserves the maximum decimal amount without IEEE-754 conversion", async () => {
    const maximum = "9999999999999999.99";
    const fetchImplementation: typeof fetch = async () =>
      Response.json({ ...draft, amount: maximum });

    await expect(
      createPaguaiDraftClient(fetchImplementation).detail(companyId, draft.id),
    ).resolves.toEqual({
      ok: true,
      value: { ...draft, amount: maximum },
    });
  });

  it("accepts an early valid calendar year in the wire response", async () => {
    const fetchImplementation: typeof fetch = async () =>
      Response.json({ ...draft, dueDate: "0001-01-01" });

    await expect(
      createPaguaiDraftClient(fetchImplementation).detail(companyId, draft.id),
    ).resolves.toMatchObject({ ok: true, value: { dueDate: "0001-01-01" } });
  });

  it("rejects an unexpected response rather than exposing it to the page", async () => {
    const fetchImplementation: typeof fetch = async () =>
      Response.json({ ...draft, unknown: "not allowed" });

    await expect(
      createPaguaiDraftClient(fetchImplementation).detail(companyId, draft.id),
    ).resolves.toEqual({ ok: false, error: "protocol" });
  });

  it("preserves a documented rate limit response for the page to handle", async () => {
    const fetchImplementation: typeof fetch = async () =>
      Response.json({ code: "rate_limited" }, { status: 429 });

    await expect(
      createPaguaiDraftClient(fetchImplementation).list(companyId),
    ).resolves.toEqual({ ok: false, error: 429 });
  });

  it.each([401, 429])(
    "preserves a bodyless %i response for the page to handle",
    async (status) => {
      const fetchImplementation: typeof fetch = async () =>
        new Response(null, { status });

      await expect(
        createPaguaiDraftClient(fetchImplementation).list(companyId),
      ).resolves.toEqual({ ok: false, error: status });
    },
  );

  it("sends the uploaded proof file id and parses persisted proof metadata back", async () => {
    const calls: [string, RequestInit][] = [];
    const proofDraft = {
      ...draft,
      proofFileId,
      proofFileName: "comprovante.pdf",
      proofFileMimeType: "application/pdf",
      proofFileSizeBytes: 10,
    };
    const fetchImplementation: typeof fetch = async (input, init) => {
      calls.push([String(input), init ?? {}]);
      return calls.length === 1
        ? Response.json({ requestToken: "csrf" })
        : Response.json(proofDraft, { status: 201 });
    };

    const result = await createPaguaiDraftClient(fetchImplementation).create({
      companyId,
      requestId,
      title: draft.title,
      description: draft.description,
      amount: draft.amount,
      currency: draft.currency,
      dueDate: draft.dueDate,
      beneficiaryDisplayName: draft.beneficiaryDisplayName,
      proofRequirement: draft.proofRequirement,
      proofFileId,
    });

    expect(result).toEqual({ ok: true, value: proofDraft });
    expect(JSON.parse(String(calls[1][1].body)).proofFileId).toBe(proofFileId);
  });

  it("keeps responses without proof fields compatible for legacy drafts", async () => {
    const fetchImplementation: typeof fetch = async () =>
      Response.json(draft, { status: 200 });

    await expect(
      createPaguaiDraftClient(fetchImplementation).detail(companyId, draft.id),
    ).resolves.toEqual({ ok: true, value: draft });
  });

  it("surfaces the typed error code from a rejected proof binding", async () => {
    const fetchImplementation: typeof fetch = async (input) =>
      String(input) === "/foundation/identity/browser-session/antiforgery"
        ? Response.json({ requestToken: "csrf" })
        : Response.json({ code: "proof_file_bound" }, { status: 409 });

    await expect(
      createPaguaiDraftClient(fetchImplementation).create({
        companyId,
        requestId,
        title: draft.title,
        description: draft.description,
        amount: draft.amount,
        currency: draft.currency,
        dueDate: draft.dueDate,
        beneficiaryDisplayName: draft.beneficiaryDisplayName,
        proofRequirement: draft.proofRequirement,
        proofFileId,
      }),
    ).resolves.toEqual({
      ok: false,
      error: { status: 409, code: "proof_file_bound" },
    });
  });
});
