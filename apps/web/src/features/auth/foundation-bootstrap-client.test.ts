import { HttpResponse, http } from "msw";
import { afterEach, describe, expect, it, vi } from "vitest";
import { server } from "../../test/msw/server";
import { createFoundationBootstrapClient } from "./foundation-bootstrap-client";

const bootstrapPath = "/foundation/bootstrap";
const testOrigin = "http://localhost";
const correlationId = "01234567-89ab-cdef-0123-456789abcdef";

const authenticatedBootstrap = {
  user: {
    id: "11111111-1111-4111-8111-111111111111",
    displayName: "Ana Ribeiro",
  },
  companies: [
    {
      membershipId: "22222222-2222-4222-8222-222222222222",
      companyId: "33333333-3333-4333-8333-333333333333",
      companyName: "Alfa",
      capabilities: [
        {
          name: "payment.approve",
          scopes: ["Company", "Self"],
        },
      ],
    },
    {
      membershipId: "44444444-4444-4444-8444-444444444444",
      companyId: "55555555-5555-4555-8555-555555555555",
      companyName: "Beta",
      capabilities: [
        {
          name: "people.view",
          scopes: ["Team", "Unit"],
        },
      ],
    },
    {
      membershipId: "66666666-6666-4666-8666-666666666666",
      companyId: "77777777-7777-4777-8777-777777777777",
      companyName: "Gama",
      capabilities: [],
    },
  ],
  flags: {
    "release.dashboard": true,
    "release.experimental": false,
  },
  platform: {
    isPlatformOperator: false,
  },
};

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

function installBootstrapResponse(
  body: unknown,
  status = 200,
  headers: HeadersInit = {},
) {
  server.use(
    http.get(handlerUrl(bootstrapPath), () => {
      if (body === null) {
        return new HttpResponse(null, { status, headers });
      }

      return HttpResponse.json(body, { status, headers });
    }),
  );
}

afterEach(() => vi.restoreAllMocks());

describe("createFoundationBootstrapClient", () => {
  it("loads the authenticated snapshot with exact company capability scopes and flags", async () => {
    const fetchSpy = createFetchSpy();
    server.use(
      http.get(handlerUrl(bootstrapPath), ({ request }) => {
        expect(request.method).toBe("GET");
        expect(request.headers.has("Authorization")).toBe(false);
        expect(request.headers.has("X-Correlation-ID")).toBe(false);
        return HttpResponse.json(authenticatedBootstrap, {
          headers: { "X-Correlation-ID": correlationId },
        });
      }),
    );
    const client = createFoundationBootstrapClient(fetchSpy);

    await expect(client.load()).resolves.toEqual({
      ok: true,
      snapshot: authenticatedBootstrap,
      correlationId,
    });

    expect(fetchSpy).toHaveBeenCalledTimes(1);
    expect(fetchSpy.mock.calls[0]?.[0]).toBe(bootstrapPath);
    expect(fetchSpy.mock.calls[0]?.[1]).toMatchObject({
      credentials: "same-origin",
      mode: "same-origin",
      redirect: "error",
      cache: "no-store",
      method: "GET",
    });
    expect(fetchSpy.mock.calls[0]?.[1]).not.toHaveProperty("body");
  });

  it("omits a malformed correlation header from an otherwise valid snapshot", async () => {
    installBootstrapResponse(authenticatedBootstrap, 200, {
      "X-Correlation-ID": "not-a-canonical-correlation-id",
    });
    const client = createFoundationBootstrapClient(createFetchSpy());

    await expect(client.load()).resolves.toEqual({
      ok: true,
      snapshot: authenticatedBootstrap,
    });
  });

  it("maps an empty 401 to anonymous without exposing a response body", async () => {
    installBootstrapResponse(null, 401);
    const client = createFoundationBootstrapClient(createFetchSpy());

    await expect(client.load()).resolves.toEqual({
      ok: false,
      error: { kind: "anonymous", status: 401 },
    });
  });

  it.each(["identity_gate_unavailable", "audit_unavailable"] as const)(
    "maps documented 503 code %s to unavailable",
    async (code) => {
      installBootstrapResponse(
        { code, message: "internal details must not escape" },
        503,
        { "X-Correlation-ID": correlationId },
      );
      const client = createFoundationBootstrapClient(createFetchSpy());

      await expect(client.load()).resolves.toEqual({
        ok: false,
        error: {
          kind: "unavailable",
          status: 503,
          code,
          correlationId,
        },
      });
    },
  );

  it("does not classify an undocumented 503 code as unavailable", async () => {
    installBootstrapResponse(
      { code: "database_secret", message: "sensitive failure" },
      503,
    );
    const client = createFoundationBootstrapClient(createFetchSpy());

    const result = await client.load();

    expect(result).toEqual({
      ok: false,
      error: { kind: "protocol", status: 503 },
    });
    expect(JSON.stringify(result)).not.toContain("sensitive");
  });

  it("maps 429 to rate_limited and preserves only a valid correlation id", async () => {
    installBootstrapResponse(null, 429, {
      "X-Correlation-ID": correlationId,
    });
    const client = createFoundationBootstrapClient(createFetchSpy());

    await expect(client.load()).resolves.toEqual({
      ok: false,
      error: { kind: "rate_limited", status: 429, correlationId },
    });
  });

  it.each([
    [
      "missing user display name",
      {
        ...authenticatedBootstrap,
        user: { id: authenticatedBootstrap.user.id },
      },
    ],
    [
      "wrong company identifier",
      {
        ...authenticatedBootstrap,
        companies: [{ ...authenticatedBootstrap.companies[0], companyId: 42 }],
      },
    ],
    [
      "wrong flags shape",
      { ...authenticatedBootstrap, flags: ["release.dashboard"] },
    ],
  ] as const)(
    "maps %s to protocol failure without a partial snapshot",
    async (_case, body) => {
      installBootstrapResponse(body);
      const client = createFoundationBootstrapClient(createFetchSpy());

      await expect(client.load()).resolves.toEqual({
        ok: false,
        error: { kind: "protocol", status: 200 },
      });
    },
  );

  it("rejects an unknown capability scope instead of widening authorization", async () => {
    installBootstrapResponse({
      ...authenticatedBootstrap,
      companies: [
        {
          ...authenticatedBootstrap.companies[0],
          capabilities: [
            {
              name: "payment.approve",
              scopes: ["Company", "AllCompanies"],
            },
          ],
        },
      ],
    });
    const client = createFoundationBootstrapClient(createFetchSpy());

    await expect(client.load()).resolves.toEqual({
      ok: false,
      error: { kind: "protocol", status: 200 },
    });
  });

  it("maps network and abort failures without leaking exception details", async () => {
    const networkFetch = vi
      .fn()
      .mockRejectedValue(new Error("secret network diagnostic"));
    const networkClient = createFoundationBootstrapClient(networkFetch);

    await expect(networkClient.load()).resolves.toEqual({
      ok: false,
      error: { kind: "network" },
    });

    const abortFetch = vi.fn().mockRejectedValue(
      Object.assign(new Error("secret abort diagnostic"), {
        name: "AbortError",
      }),
    );
    const abortClient = createFoundationBootstrapClient(abortFetch);
    const result = await abortClient.load(new AbortController().signal);

    expect(result).toEqual({
      ok: false,
      error: { kind: "aborted" },
    });
    expect(JSON.stringify(result)).not.toContain("secret");
  });

  it("does not retry a failed bootstrap request", async () => {
    const fetchSpy = createFetchSpy();
    installBootstrapResponse({ code: "audit_unavailable" }, 503);
    const client = createFoundationBootstrapClient(fetchSpy);

    await client.load();

    expect(fetchSpy).toHaveBeenCalledTimes(1);
    expect(fetchSpy.mock.calls.map(([input]) => String(input))).toEqual([
      bootstrapPath,
    ]);
  });
});
