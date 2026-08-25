import { afterEach, describe, expect, it, vi } from "vitest";
import { createAgentRunClient } from "./agent-client";

const antiforgeryPath = "/foundation/identity/browser-session/antiforgery";
const agentRunsPath = "/agent/runs";

const jsonHeaders = { "Content-Type": "application/json" };

function tokenResponse() {
  return new Response(JSON.stringify({ requestToken: "csrf-token" }), {
    status: 200,
    headers: jsonHeaders,
  });
}

function runResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: jsonHeaders,
  });
}

function typedBody(overrides: Record<string, unknown> = {}) {
  return {
    status: "result",
    reasonCode: "ok",
    text: "Resposta do modelo",
    sources: null,
    correlationId: "0192b0c0-0000-7000-8000-000000000001",
    ...overrides,
  };
}

afterEach(() => vi.unstubAllGlobals());

describe("agent run client", () => {
  it("posts the approved journey with a fresh requestId and the company when present", async () => {
    const fetchSpy = vi
      .fn()
      .mockResolvedValueOnce(tokenResponse())
      .mockResolvedValueOnce(runResponse(typedBody()));
    vi.stubGlobal("fetch", fetchSpy);
    const client = createAgentRunClient();

    const result = await client.send({
      companyId: "33333333-3333-4333-8333-333333333333",
      message: "Resuma a solicitação",
    });

    expect(result).toMatchObject({ ok: true, run: { status: "result" } });
    expect(fetchSpy.mock.calls).toHaveLength(2);
    expect(fetchSpy.mock.calls[0][0]).toBe(antiforgeryPath);
    const [path, init] = fetchSpy.mock.calls[1];
    expect(path).toBe(agentRunsPath);
    expect(init).toEqual(
      expect.objectContaining({
        cache: "no-store",
        credentials: "same-origin",
        headers: {
          Accept: "application/json",
          "Content-Type": "application/json",
          "X-CSRF-TOKEN": "csrf-token",
        },
        method: "POST",
        mode: "same-origin",
        redirect: "error",
      }),
    );
    expect(JSON.parse(init.body as string)).toEqual({
      companyId: "33333333-3333-4333-8333-333333333333",
      journey: "read_only_summarize_v1",
      message: "Resuma a solicitação",
      requestId: expect.stringMatching(
        /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/,
      ),
    });
  });

  it("omits companyId from the body when no company is available", async () => {
    const fetchSpy = vi
      .fn()
      .mockResolvedValueOnce(tokenResponse())
      .mockResolvedValueOnce(runResponse(typedBody()));
    vi.stubGlobal("fetch", fetchSpy);
    const client = createAgentRunClient();

    await client.send({ message: "Sem empresa" });

    const body = JSON.parse(fetchSpy.mock.calls[1][1].body as string) as Record<
      string,
      unknown
    >;
    expect(body).not.toHaveProperty("companyId");
    expect(body).toMatchObject({ journey: "read_only_summarize_v1" });
  });

  it("preserves the current no_evidence clarification reason", async () => {
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValueOnce(tokenResponse())
        .mockResolvedValueOnce(
          runResponse(
            typedBody({
              status: "needs_clarification",
              reasonCode: "no_evidence",
              text: null,
            }),
          ),
        ),
    );
    const client = createAgentRunClient();

    const result = await client.send({ message: "Resuma sem evidência" });

    expect(result).toMatchObject({
      ok: true,
      run: {
        status: "needs_clarification",
        reasonCode: "no_evidence",
        text: null,
        sources: null,
      },
    });
  });

  it("keeps nullable contract fields present and maps typed 400 validation errors", async () => {
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValueOnce(tokenResponse())
        .mockResolvedValueOnce(
          runResponse(
            typedBody({
              status: "validation_error",
              reasonCode: "message_too_long",
              text: null,
            }),
            400,
          ),
        ),
    );
    const client = createAgentRunClient();

    const result = await client.send({ message: "x".repeat(4001) });

    expect(result).toMatchObject({
      ok: true,
      run: {
        status: "validation_error",
        reasonCode: "message_too_long",
        text: null,
        sources: null,
      },
    });
  });

  it("maps the antiforgery 400 envelope to a typed antiforgery failure", async () => {
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValueOnce(tokenResponse())
        .mockResolvedValueOnce(runResponse({ code: "invalid_request" }, 400)),
    );
    const client = createAgentRunClient();

    const result = await client.send({ message: "qualquer" });

    expect(result).toEqual({
      ok: false,
      failure: { kind: "antiforgery", status: 400, code: "invalid_request" },
    });
  });

  it("maps typed 409 conflicts without exposing the payload", async () => {
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValueOnce(tokenResponse())
        .mockResolvedValueOnce(
          runResponse(
            typedBody({
              status: "conflict",
              reasonCode: "idempotency_conflict",
              text: null,
            }),
            409,
          ),
        ),
    );
    const client = createAgentRunClient();

    const result = await client.send({ message: "replay divergente" });

    expect(result).toMatchObject({
      ok: true,
      run: { status: "conflict", reasonCode: "idempotency_conflict" },
    });
  });

  it("fails typed without parsing any body for 401 and 429", async () => {
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValueOnce(new Response(null, { status: 401 }))
        .mockResolvedValueOnce(tokenResponse())
        .mockResolvedValueOnce(new Response(null, { status: 429 })),
    );
    const client = createAgentRunClient();

    await expect(client.send({ message: "a" })).resolves.toEqual({
      ok: false,
      failure: { kind: "unauthorized", status: 401 },
    });
    await expect(client.send({ message: "b" })).resolves.toEqual({
      ok: false,
      failure: { kind: "rate_limited", status: 429 },
    });
  });

  it("maps allow-listed 503/504 envelope codes", async () => {
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValueOnce(tokenResponse())
        .mockResolvedValueOnce(
          runResponse({ code: "provider_not_configured" }, 503),
        )
        .mockResolvedValueOnce(tokenResponse())
        .mockResolvedValueOnce(runResponse({ code: "agent_timeout" }, 504)),
    );
    const client = createAgentRunClient();

    await expect(client.send({ message: "a" })).resolves.toEqual({
      ok: false,
      failure: {
        kind: "unavailable",
        status: 503,
        code: "provider_not_configured",
      },
    });
    await expect(client.send({ message: "b" })).resolves.toEqual({
      ok: false,
      failure: { kind: "timeout", status: 504, code: "agent_timeout" },
    });
  });

  it("turns unknown bodies and statuses into protocol failures without echoing content", async () => {
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValueOnce(tokenResponse())
        .mockResolvedValueOnce(runResponse({ unexpected: "shape" }))
        .mockResolvedValueOnce(tokenResponse())
        .mockResolvedValueOnce(runResponse({ code: "totally_unknown" }, 503))
        .mockResolvedValueOnce(tokenResponse())
        .mockResolvedValueOnce(new Response("nope", { status: 500 })),
    );
    const client = createAgentRunClient();

    await expect(client.send({ message: "a" })).resolves.toEqual({
      ok: false,
      failure: { kind: "protocol", status: 200 },
    });
    await expect(client.send({ message: "b" })).resolves.toEqual({
      ok: false,
      failure: { kind: "protocol", status: 503 },
    });
    await expect(client.send({ message: "c" })).resolves.toEqual({
      ok: false,
      failure: { kind: "protocol", status: 500 },
    });
  });

  it("maps network errors and aborts to typed failures without retrying", async () => {
    const controller = new AbortController();
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValueOnce(tokenResponse())
        .mockImplementationOnce(() => Promise.reject(new TypeError("fail"))),
    );
    const client = createAgentRunClient();

    await expect(client.send({ message: "a" })).resolves.toEqual({
      ok: false,
      failure: { kind: "network" },
    });

    controller.abort();
    await expect(
      client.send({ message: "b", signal: controller.signal }),
    ).resolves.toEqual({ ok: false, failure: { kind: "aborted" } });
    expect(
      vi
        .mocked(globalThis.fetch)
        .mock.calls.filter(([path]) => path === agentRunsPath),
    ).toHaveLength(1);
  });

  it("stops before the mutation when the antiforgery token is unavailable", async () => {
    const fetchSpy = vi
      .fn()
      .mockResolvedValue(new Response(null, { status: 401 }));
    vi.stubGlobal("fetch", fetchSpy);
    const client = createAgentRunClient();

    await expect(client.send({ message: "a" })).resolves.toEqual({
      ok: false,
      failure: { kind: "unauthorized", status: 401 },
    });
    expect(fetchSpy.mock.calls).toHaveLength(1);
    expect(fetchSpy.mock.calls[0][0]).toBe(antiforgeryPath);
  });

  it("rejects crossed status/HTTP combinations as protocol failures", async () => {
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValueOnce(tokenResponse())
        .mockResolvedValueOnce(runResponse(typedBody({ status: "conflict" })))
        .mockResolvedValueOnce(tokenResponse())
        .mockResolvedValueOnce(
          runResponse(typedBody({ status: "result" }), 409),
        ),
    );
    const client = createAgentRunClient();

    await expect(client.send({ message: "a" })).resolves.toEqual({
      ok: false,
      failure: { kind: "protocol", status: 200 },
    });
    await expect(client.send({ message: "b" })).resolves.toEqual({
      ok: false,
      failure: { kind: "protocol", status: 409 },
    });
  });

  it("rejects typed bodies whose correlationId is not a canonical uuid", async () => {
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValueOnce(tokenResponse())
        .mockResolvedValueOnce(
          runResponse(typedBody({ correlationId: "not-a-uuid" })),
        ),
    );
    const client = createAgentRunClient();

    await expect(client.send({ message: "a" })).resolves.toEqual({
      ok: false,
      failure: { kind: "protocol", status: 200 },
    });
  });

  it("normalizes unknown reasonCodes to null instead of echoing them", async () => {
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValueOnce(tokenResponse())
        .mockResolvedValueOnce(
          runResponse(typedBody({ reasonCode: "totally_new_code" })),
        ),
    );
    const client = createAgentRunClient();

    const result = await client.send({ message: "a" });

    expect(result).toMatchObject({
      ok: true,
      run: { reasonCode: null, text: "Resposta do modelo" },
    });
  });

  it("rejects envelope codes that belong to another HTTP status or extra fields", async () => {
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValueOnce(tokenResponse())
        .mockResolvedValueOnce(runResponse({ code: "agent_timeout" }, 503))
        .mockResolvedValueOnce(tokenResponse())
        .mockResolvedValueOnce(runResponse({ code: "audit_unavailable" }, 504))
        .mockResolvedValueOnce(tokenResponse())
        .mockResolvedValueOnce(
          runResponse({ code: "invalid_request", extra: 1 }, 400),
        ),
    );
    const client = createAgentRunClient();

    await expect(client.send({ message: "a" })).resolves.toEqual({
      ok: false,
      failure: { kind: "protocol", status: 503 },
    });
    await expect(client.send({ message: "b" })).resolves.toEqual({
      ok: false,
      failure: { kind: "protocol", status: 504 },
    });
    await expect(client.send({ message: "c" })).resolves.toEqual({
      ok: false,
      failure: { kind: "protocol", status: 400 },
    });
  });

  it("copies attachment IDs into the agent request only when attachments are supplied", async () => {
    let resolveToken: (response: Response) => void = () => {};
    const token = new Promise<Response>((resolve) => (resolveToken = resolve));
    const fetchSpy = vi
      .fn()
      .mockImplementationOnce(() => token)
      .mockResolvedValueOnce(runResponse(typedBody()));
    vi.stubGlobal("fetch", fetchSpy);
    const client = createAgentRunClient();
    const attachments = ["44444444-4444-4444-8444-444444444444"];

    const pending = client.send({
      attachments,
      message: "Resuma o documento selecionado",
    });
    attachments.push("55555555-5555-4555-8555-555555555555");
    resolveToken(tokenResponse());
    await expect(pending).resolves.toMatchObject({ ok: true });

    const body = JSON.parse(fetchSpy.mock.calls[1][1].body as string);
    expect(body.attachments).toEqual(["44444444-4444-4444-8444-444444444444"]);
  });

  it("preserves the document-context clarification reason from the typed contract", async () => {
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValueOnce(tokenResponse())
        .mockResolvedValueOnce(
          runResponse(
            typedBody({
              reasonCode: "document_context_unavailable",
              status: "needs_clarification",
              text: null,
            }),
          ),
        ),
    );
    const client = createAgentRunClient();

    const result = await client.send({ message: "Resuma o anexo" });

    expect(result).toMatchObject({
      ok: true,
      run: {
        reasonCode: "document_context_unavailable",
        status: "needs_clarification",
      },
    });
  });
});
