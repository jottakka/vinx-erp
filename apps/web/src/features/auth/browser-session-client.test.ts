import { HttpResponse, http } from "msw";
import { afterEach, describe, expect, it, vi } from "vitest";
import { server } from "../../test/msw/server";
import { createBrowserSessionClient } from "./browser-session-client";

const antiForgeryPath = "/foundation/identity/browser-session/antiforgery";
const sessionPath = "/foundation/identity/browser-session";
const testOrigin = "http://localhost";
const correlationId = "01234567-89ab-cdef-0123-456789abcdef";

function handlerUrl(path: string) {
  return new URL(path, testOrigin).toString();
}

function createFetchSpy() {
  return vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const forwardedInput =
      typeof input === "string" ? new URL(input, "http://localhost") : input;

    return globalThis.fetch(forwardedInput, init);
  });
}

function installTokenHandler(
  token: string | null = "csrf-token",
  status = 200,
) {
  server.use(
    http.get(handlerUrl(antiForgeryPath), () =>
      token === null
        ? new HttpResponse(null, { status })
        : HttpResponse.json({ requestToken: token }, { status }),
    ),
  );
}

afterEach(() => vi.restoreAllMocks());

describe("createBrowserSessionClient", () => {
  it("gets antiforgery first, then posts exact credentials and returns the correlation id", async () => {
    const requests: Request[] = [];
    installTokenHandler();
    server.use(
      http.post(handlerUrl(sessionPath), async ({ request }) => {
        requests.push(request);
        expect(await request.json()).toEqual({
          email: "maria@example.com",
          password: "correct horse battery staple",
        });
        expect(request.headers.get("X-CSRF-TOKEN")).toBe("csrf-token");
        expect(request.headers.has("Authorization")).toBe(false);
        expect(request.headers.has("X-Correlation-ID")).toBe(false);
        return new HttpResponse(null, {
          status: 204,
          headers: { "X-Correlation-ID": correlationId },
        });
      }),
    );
    const fetchSpy = createFetchSpy();
    const client = createBrowserSessionClient(fetchSpy);

    await expect(
      client.signIn({
        email: "maria@example.com",
        password: "correct horse battery staple",
      }),
    ).resolves.toEqual({ ok: true, correlationId });

    expect(requests).toHaveLength(1);
    expect(fetchSpy.mock.calls.map(([input]) => String(input))).toEqual([
      antiForgeryPath,
      sessionPath,
    ]);
    expect(fetchSpy.mock.calls[0]?.[1]).toMatchObject({
      credentials: "same-origin",
      mode: "same-origin",
      redirect: "error",
      cache: "no-store",
    });
    expect(fetchSpy.mock.calls[1]?.[1]).toMatchObject({
      credentials: "same-origin",
      mode: "same-origin",
      redirect: "error",
      cache: "no-store",
      headers: {
        "Content-Type": "application/json",
        "X-CSRF-TOKEN": "csrf-token",
      },
    });
  });

  it("gets antiforgery before deleting the session and accepts an empty 204 response", async () => {
    const requests: string[] = [];
    installTokenHandler();
    server.use(
      http.get(handlerUrl(antiForgeryPath), () => {
        requests.push("token");
        return HttpResponse.json({ requestToken: "csrf-token" });
      }),
      http.delete(handlerUrl(sessionPath), ({ request }) => {
        requests.push("delete");
        expect(request.headers.get("X-CSRF-TOKEN")).toBe("csrf-token");
        expect(request.headers.get("Content-Type")).toBeNull();
        return new HttpResponse(null, {
          status: 204,
          headers: { "X-Correlation-ID": correlationId },
        });
      }),
    );
    const client = createBrowserSessionClient(createFetchSpy());

    await expect(client.signOut()).resolves.toEqual({
      ok: true,
      correlationId,
    });
    expect(requests).toEqual(["token", "delete"]);
  });

  it("omits malformed correlation ids while preserving canonical lower-case UUIDs", async () => {
    installTokenHandler();
    server.use(
      http.post(
        handlerUrl(sessionPath),
        () =>
          new HttpResponse(null, {
            status: 204,
            headers: { "X-Correlation-ID": "not-a-correlation-id" },
          }),
      ),
    );
    const client = createBrowserSessionClient(createFetchSpy());

    await expect(
      client.signIn({ email: "maria@example.com", password: "secret" }),
    ).resolves.toEqual({ ok: true });
  });

  it.each([
    [401, "unauthorized"],
    [403, "forbidden"],
    [429, "rate_limited"],
  ] as const)("normalizes an empty %s response as %s", async (status, kind) => {
    installTokenHandler();
    server.use(
      http.post(
        handlerUrl(sessionPath),
        () => new HttpResponse(null, { status }),
      ),
    );
    const client = createBrowserSessionClient(createFetchSpy());

    await expect(
      client.signIn({ email: "maria@example.com", password: "secret" }),
    ).resolves.toEqual({ ok: false, error: { kind, status } });
  });

  it.each([
    "identity_provider_unavailable",
    "identity_gate_unavailable",
  ] as const)("normalizes documented 503 code %s safely", async (code) => {
    installTokenHandler();
    server.use(
      http.post(handlerUrl(sessionPath), () =>
        HttpResponse.json(
          { code, message: "provider internals must not escape" },
          { status: 503, headers: { "X-Correlation-ID": correlationId } },
        ),
      ),
    );
    const client = createBrowserSessionClient(createFetchSpy());

    await expect(
      client.signIn({ email: "maria@example.com", password: "secret" }),
    ).resolves.toEqual({
      ok: false,
      error: { kind: "unavailable", status: 503, code, correlationId },
    });
  });

  it("does not leak malformed error bodies, status text, or server messages", async () => {
    installTokenHandler();
    server.use(
      http.post(
        handlerUrl(sessionPath),
        () =>
          new HttpResponse(
            JSON.stringify({
              message: "secret internal message",
              stack: "secret stack",
              token: "secret token",
            }),
            {
              status: 418,
              statusText: "secret status text",
              headers: { "Content-Type": "application/json" },
            },
          ),
      ),
    );
    const client = createBrowserSessionClient(createFetchSpy());

    const result = await client.signIn({
      email: "maria@example.com",
      password: "secret",
    });

    expect(result).toEqual({
      ok: false,
      error: { kind: "protocol", status: 418 },
    });
    expect(JSON.stringify(result)).not.toContain("secret");
  });

  it("returns protocol failure and does not mutate when the antiforgery token is malformed", async () => {
    const fetchSpy = createFetchSpy();
    server.use(
      http.get(handlerUrl(antiForgeryPath), () =>
        HttpResponse.json({ nope: true }),
      ),
    );
    const client = createBrowserSessionClient(fetchSpy);

    await expect(
      client.signIn({ email: "maria@example.com", password: "secret" }),
    ).resolves.toEqual({ ok: false, error: { kind: "protocol" } });
    expect(fetchSpy).toHaveBeenCalledTimes(1);
  });

  it("does not mutate after antiforgery failure", async () => {
    const fetchSpy = createFetchSpy();
    installTokenHandler(null, 503);
    const client = createBrowserSessionClient(fetchSpy);

    await expect(client.signOut()).resolves.toMatchObject({
      ok: false,
      error: { kind: "unavailable", status: 503 },
    });
    expect(fetchSpy).toHaveBeenCalledTimes(1);
  });

  it("maps network and abort failures without exposing exception details", async () => {
    const networkFetch = vi
      .fn()
      .mockRejectedValue(new Error("secret socket detail"));
    const networkClient = createBrowserSessionClient(networkFetch);
    await expect(networkClient.signOut()).resolves.toEqual({
      ok: false,
      error: { kind: "network" },
    });

    const abortFetch = vi
      .fn()
      .mockRejectedValue(
        Object.assign(new Error("secret abort detail"), { name: "AbortError" }),
      );
    const abortClient = createBrowserSessionClient(abortFetch);
    const result = await abortClient.signOut(new AbortController().signal);
    expect(result).toEqual({ ok: false, error: { kind: "aborted" } });
    expect(JSON.stringify(result)).not.toContain("secret");
  });

  it("does not start antiforgery when the signal is already aborted", async () => {
    const fetchSpy = createFetchSpy();
    const controller = new AbortController();
    controller.abort();
    const client = createBrowserSessionClient(fetchSpy);

    await expect(client.signOut(controller.signal)).resolves.toEqual({
      ok: false,
      error: { kind: "aborted" },
    });
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it("does not mutate when aborted while antiforgery JSON is pending", async () => {
    let rejectJson!: (reason?: unknown) => void;
    const jsonPending = new Promise<unknown>((_resolve, reject) => {
      rejectJson = reject;
    });
    const calls: RequestInit[] = [];
    const fetchSpy = vi.fn(
      async (_input: RequestInfo | URL, init?: RequestInit) => {
        calls.push(init ?? {});
        if (init?.method === "GET") {
          const response = new Response(null, {
            status: 200,
            headers: { "Content-Type": "application/json" },
          });
          Object.defineProperty(response, "json", {
            value: () => jsonPending,
          });

          return response;
        }

        return new Response(null, { status: 204 });
      },
    );
    const controller = new AbortController();
    const client = createBrowserSessionClient(fetchSpy);
    const pendingSignOut = client.signOut(controller.signal);

    await vi.waitFor(() => expect(fetchSpy).toHaveBeenCalledTimes(1));
    controller.abort();
    rejectJson(Object.assign(new Error("aborted"), { name: "AbortError" }));

    await expect(pendingSignOut).resolves.toEqual({
      ok: false,
      error: { kind: "aborted" },
    });
    expect(calls.filter((init) => init.method === "DELETE")).toHaveLength(0);
  });

  it("rejects invalid credentials locally without any fetch", async () => {
    const fetchSpy = createFetchSpy();
    const client = createBrowserSessionClient(fetchSpy);

    await expect(client.signIn({ email: "", password: "" })).resolves.toEqual({
      ok: false,
      error: { kind: "invalid_request" },
    });
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it("does not retry a failed mutation", async () => {
    const fetchSpy = createFetchSpy();
    installTokenHandler();
    server.use(
      http.post(
        handlerUrl(sessionPath),
        () => new HttpResponse(null, { status: 500 }),
      ),
    );
    const client = createBrowserSessionClient(fetchSpy);

    await client.signIn({ email: "maria@example.com", password: "secret" });

    expect(fetchSpy).toHaveBeenCalledTimes(2);
    expect(fetchSpy.mock.calls.map(([input]) => String(input))).toEqual([
      antiForgeryPath,
      sessionPath,
    ]);
  });

  it("keeps concurrent sign-ins isolated with their own antiforgery tokens", async () => {
    const seen: Array<{ email: string; token: string }> = [];
    let tokenNumber = 0;
    installTokenHandler();
    server.use(
      http.get(handlerUrl(antiForgeryPath), () => {
        tokenNumber += 1;
        return HttpResponse.json({ requestToken: `csrf-${tokenNumber}` });
      }),
      http.post(handlerUrl(sessionPath), async ({ request }) => {
        const body = (await request.json()) as { email: string };
        seen.push({
          email: body.email,
          token: request.headers.get("X-CSRF-TOKEN") ?? "",
        });
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const client = createBrowserSessionClient(createFetchSpy());

    await Promise.all([
      client.signIn({ email: "first@example.com", password: "secret" }),
      client.signIn({ email: "second@example.com", password: "secret" }),
    ]);

    expect(seen).toEqual(
      expect.arrayContaining([
        { email: "first@example.com", token: "csrf-1" },
        { email: "second@example.com", token: "csrf-2" },
      ]),
    );
  });
});
