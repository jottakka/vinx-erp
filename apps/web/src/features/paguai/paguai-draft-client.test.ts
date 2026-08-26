import { describe, expect, it } from "vitest";
import { createPaguaiDraftClient } from "./paguai-draft-client";

const companyId = "11111111-1111-4111-8111-111111111111";
const requestId = "22222222-2222-4222-8222-222222222222";
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

describe("Paguai draft client", () => {
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
});
