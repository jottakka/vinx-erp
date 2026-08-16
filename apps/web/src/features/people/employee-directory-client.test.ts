import { HttpResponse, http } from "msw";
import { afterEach, describe, expect, it, vi } from "vitest";
import { server } from "../../test/msw/server";
import { createEmployeeDirectoryClient } from "./employee-directory-client";

const antiforgeryPath = "/foundation/identity/browser-session/antiforgery";
const companyId = "01234567-89ab-cdef-0123-456789abcdef";
const requestId = "fedcba98-7654-3210-fedc-ba9876543210";
const employeeId = "11111111-2222-3333-4444-555555555555";
const membershipId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
const correlationId = "12345678-1234-1234-1234-123456789abc";
const emptyUuid = "00000000-0000-0000-0000-000000000000";
const employeesPath = `/foundation/companies/${companyId}/employees`;
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

function validCommand() {
  return {
    companyId,
    displayName: "Maria da Silva",
    email: "maria@example.com",
    requestId,
  };
}

afterEach(() => vi.restoreAllMocks());

describe("createEmployeeDirectoryClient", () => {
  it("lists only the documented directory items through the fixed company path", async () => {
    server.use(
      http.get(handlerUrl(employeesPath), ({ request }) => {
        expect(request.headers.get("Authorization")).toBeNull();
        return HttpResponse.json(
          [
            {
              displayName: "Maria da Silva",
              employeeId,
              membershipId,
            },
          ],
          { headers: { "X-Correlation-ID": correlationId } },
        );
      }),
    );
    const fetchSpy = createFetchSpy();
    const client = createEmployeeDirectoryClient(fetchSpy);

    await expect(client.list(companyId)).resolves.toEqual({
      correlationId,
      items: [{ displayName: "Maria da Silva", employeeId, membershipId }],
      ok: true,
    });
    expect(fetchSpy.mock.calls.map(([path]) => String(path))).toEqual([
      employeesPath,
    ]);
    expect(fetchSpy.mock.calls[0]?.[1]).toMatchObject({
      cache: "no-store",
      credentials: "same-origin",
      method: "GET",
      mode: "same-origin",
      redirect: "error",
    });
  });

  it("gets a fresh antiforgery token before a single strict create request and hides identifiers", async () => {
    const requests: Request[] = [];
    server.use(
      http.get(handlerUrl(antiforgeryPath), () =>
        HttpResponse.json({ requestToken: "fresh-token" }),
      ),
      http.post(handlerUrl(employeesPath), async ({ request }) => {
        requests.push(request);
        expect(await request.json()).toEqual(validCommand());
        expect(request.headers.get("X-CSRF-TOKEN")).toBe("fresh-token");
        expect(request.headers.get("Content-Type")).toBe("application/json");
        expect(request.headers.has("Authorization")).toBe(false);
        return HttpResponse.json(
          {
            employeeId,
            membershipId,
            userId: "99999999-8888-7777-6666-555555555555",
          },
          { status: 201, headers: { "X-Correlation-ID": correlationId } },
        );
      }),
    );
    const fetchSpy = createFetchSpy();
    const client = createEmployeeDirectoryClient(fetchSpy);

    await expect(client.create(validCommand())).resolves.toEqual({
      correlationId,
      ok: true,
      state: "created",
    });
    expect(requests).toHaveLength(1);
    expect(fetchSpy.mock.calls.map(([path]) => String(path))).toEqual([
      antiforgeryPath,
      employeesPath,
    ]);
    expect(fetchSpy.mock.calls[1]?.[1]).toMatchObject({
      cache: "no-store",
      credentials: "same-origin",
      mode: "same-origin",
      redirect: "error",
    });
  });

  it("normalizes a 200 create response as replay without exposing its identifiers", async () => {
    server.use(
      http.get(handlerUrl(antiforgeryPath), () =>
        HttpResponse.json({ requestToken: "fresh-token" }),
      ),
      http.post(handlerUrl(employeesPath), () =>
        HttpResponse.json({ employeeId, membershipId, userId: employeeId }),
      ),
    );

    await expect(
      createEmployeeDirectoryClient(createFetchSpy()).create(validCommand()),
    ).resolves.toEqual({ ok: true, state: "replay" });
  });

  it("rejects malformed UUIDs and commands locally without HTTP", async () => {
    const fetchSpy = createFetchSpy();
    const client = createEmployeeDirectoryClient(fetchSpy);

    await expect(client.list("not-a-uuid")).resolves.toEqual({
      error: { kind: "invalid_request" },
      ok: false,
    });
    await expect(
      client.create({ ...validCommand(), email: "not-an-email" }),
    ).resolves.toEqual({ error: { kind: "invalid_request" }, ok: false });
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it("rejects empty company and request UUIDs locally without HTTP", async () => {
    const fetchSpy = createFetchSpy();
    const client = createEmployeeDirectoryClient(fetchSpy);

    await expect(client.list(emptyUuid)).resolves.toEqual({
      error: { kind: "invalid_request" },
      ok: false,
    });
    await expect(
      client.create({ ...validCommand(), companyId: emptyUuid }),
    ).resolves.toEqual({ error: { kind: "invalid_request" }, ok: false });
    await expect(
      client.create({ ...validCommand(), requestId: emptyUuid }),
    ).resolves.toEqual({ error: { kind: "invalid_request" }, ok: false });
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it("fails closed for incompatible success JSON", async () => {
    server.use(
      http.get(handlerUrl(employeesPath), () =>
        HttpResponse.json([
          {
            employeeId,
            membershipId,
            displayName: "Maria",
            email: "private@example.com",
          },
        ]),
      ),
      http.get(handlerUrl(antiforgeryPath), () =>
        HttpResponse.json({ requestToken: "fresh-token" }),
      ),
      http.post(handlerUrl(employeesPath), () =>
        HttpResponse.json({ employeeId }),
      ),
    );
    const client = createEmployeeDirectoryClient(createFetchSpy());

    await expect(client.list(companyId)).resolves.toEqual({
      error: { kind: "protocol", status: 200 },
      ok: false,
    });
    await expect(client.create(validCommand())).resolves.toEqual({
      error: { kind: "protocol", status: 200 },
      ok: false,
    });
  });

  it.each([
    [401, "unauthorized"],
    [403, "forbidden"],
    [429, "rate_limited"],
  ] as const)("normalizes an empty %s response as %s", async (status, kind) => {
    server.use(
      http.get(
        handlerUrl(employeesPath),
        () => new HttpResponse(null, { status }),
      ),
    );

    await expect(
      createEmployeeDirectoryClient(createFetchSpy()).list(companyId),
    ).resolves.toEqual({ error: { kind, status }, ok: false });
  });

  it.each([
    ["employee_conflict", "employee_conflict"],
    ["idempotency_conflict", "idempotency_conflict"],
  ] as const)("normalizes documented 409 code %s", async (code, kind) => {
    server.use(
      http.get(handlerUrl(antiforgeryPath), () =>
        HttpResponse.json({ requestToken: "fresh-token" }),
      ),
      http.post(handlerUrl(employeesPath), () =>
        HttpResponse.json(
          { code, detail: "must not reach the UI" },
          { status: 409, headers: { "X-Correlation-ID": correlationId } },
        ),
      ),
    );

    const result = await createEmployeeDirectoryClient(createFetchSpy()).create(
      validCommand(),
    );

    expect(result).toEqual({
      error: { correlationId, kind, status: 409 },
      ok: false,
    });
    expect(JSON.stringify(result)).not.toContain("must not reach the UI");
  });

  it("fails closed for an unknown create conflict without leaking its body", async () => {
    const rawDetail = "unknown conflict internals";
    server.use(
      http.get(handlerUrl(antiforgeryPath), () =>
        HttpResponse.json({ requestToken: "fresh-token" }),
      ),
      http.post(handlerUrl(employeesPath), () =>
        HttpResponse.json(
          { code: "unknown_conflict", detail: rawDetail },
          { status: 409 },
        ),
      ),
    );

    const result = await createEmployeeDirectoryClient(createFetchSpy()).create(
      validCommand(),
    );

    expect(result).toEqual({
      error: { kind: "protocol", status: 409 },
      ok: false,
    });
    expect(JSON.stringify(result)).not.toContain(rawDetail);
  });

  it("fails closed for undocumented conflicts and unavailable responses", async () => {
    server.use(
      http.get(handlerUrl(employeesPath), () =>
        HttpResponse.json(
          { code: "unknown", message: "internal details" },
          { status: 503, headers: { "X-Correlation-ID": "not-a-uuid" } },
        ),
      ),
      http.get(handlerUrl(antiforgeryPath), () =>
        HttpResponse.json({ code: "unexpected" }, { status: 503 }),
      ),
    );
    const client = createEmployeeDirectoryClient(createFetchSpy());

    await expect(client.list(companyId)).resolves.toEqual({
      error: { kind: "unavailable", status: 503 },
      ok: false,
    });
    await expect(client.create(validCommand())).resolves.toEqual({
      error: { kind: "unavailable", status: 503 },
      ok: false,
    });
  });

  it("maps already-aborted signals without starting a request", async () => {
    const fetchSpy = createFetchSpy();
    const controller = new AbortController();
    controller.abort();

    await expect(
      createEmployeeDirectoryClient(fetchSpy).list(
        companyId,
        controller.signal,
      ),
    ).resolves.toEqual({ error: { kind: "aborted" }, ok: false });
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it("maps network failures without exposing exception text", async () => {
    const client = createEmployeeDirectoryClient(
      vi.fn().mockRejectedValue(new Error("secret socket details")),
    );

    const result = await client.list(companyId);

    expect(result).toEqual({ error: { kind: "network" }, ok: false });
    expect(JSON.stringify(result)).not.toContain("secret");
  });
});
