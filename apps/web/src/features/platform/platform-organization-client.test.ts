import { HttpResponse, http } from "msw";
import { afterEach, describe, expect, it, vi } from "vitest";
import { server } from "../../test/msw/server";
import { createPlatformOrganizationClient } from "./platform-organization-client";

const antiforgeryPath = "/foundation/identity/browser-session/antiforgery";
const provisioningPath = "/foundation/platform/organizations";
const requestId = "fedcba98-7654-3210-fedc-ba9876543210";
const correlationId = "12345678-1234-1234-1234-123456789abc";
const emptyUuid = "00000000-0000-0000-0000-000000000000";
const testOrigin = "http://localhost";

const provisionReceipt = {
  groupId: "11111111-2222-3333-4444-555555555555",
  companyId: "22222222-3333-4444-5555-666666666666",
  ownerUserId: "33333333-4444-5555-6666-777777777777",
  ownerMembershipId: "44444444-5555-6666-7777-888888888888",
  ownerInvitationId: "55555555-6666-7777-8888-999999999999",
  invitationStatus: "pending_delivery",
} as const;

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
    requestId,
    groupName: "Northwind Group",
    companyName: "Northwind HQ",
    ownerDisplayName: "Owner",
    ownerEmail: "owner@example.test",
  };
}

afterEach(() => vi.restoreAllMocks());

describe("createPlatformOrganizationClient", () => {
  it("gets a fresh antiforgery token before a single strict provision request and hides identifiers", async () => {
    const requests: Request[] = [];
    server.use(
      http.get(handlerUrl(antiforgeryPath), () =>
        HttpResponse.json({ requestToken: "fresh-token" }),
      ),
      http.post(handlerUrl(provisioningPath), async ({ request }) => {
        requests.push(request);
        expect(await request.json()).toEqual(validCommand());
        expect(request.headers.get("X-CSRF-TOKEN")).toBe("fresh-token");
        expect(request.headers.get("Content-Type")).toBe("application/json");
        expect(request.headers.has("Authorization")).toBe(false);
        return HttpResponse.json(provisionReceipt, {
          status: 201,
          headers: { "X-Correlation-ID": correlationId },
        });
      }),
    );
    const fetchSpy = createFetchSpy();
    const client = createPlatformOrganizationClient(fetchSpy);

    await expect(client.provision(validCommand())).resolves.toEqual({
      correlationId,
      invitationStatus: "pending_delivery",
      ok: true,
      state: "created",
    });
    expect(requests).toHaveLength(1);
    expect(fetchSpy.mock.calls.map(([path]) => String(path))).toEqual([
      antiforgeryPath,
      provisioningPath,
    ]);
    expect(fetchSpy.mock.calls[1]?.[1]).toMatchObject({
      cache: "no-store",
      credentials: "same-origin",
      method: "POST",
      mode: "same-origin",
      redirect: "error",
    });
  });

  it("normalizes a 200 provision response as replay without exposing identifiers", async () => {
    server.use(
      http.get(handlerUrl(antiforgeryPath), () =>
        HttpResponse.json({ requestToken: "fresh-token" }),
      ),
      http.post(handlerUrl(provisioningPath), () =>
        HttpResponse.json(provisionReceipt),
      ),
    );

    const result = await createPlatformOrganizationClient(
      createFetchSpy(),
    ).provision(validCommand());

    expect(result).toEqual({
      ok: true,
      state: "replay",
      invitationStatus: "pending_delivery",
    });
    expect(JSON.stringify(result)).not.toContain(provisionReceipt.groupId);
    expect(JSON.stringify(result)).not.toContain(
      provisionReceipt.ownerInvitationId,
    );
  });

  it("rejects malformed commands locally without HTTP", async () => {
    const fetchSpy = createFetchSpy();
    const client = createPlatformOrganizationClient(fetchSpy);

    await expect(
      client.provision({ ...validCommand(), requestId: "not-a-uuid" }),
    ).resolves.toEqual({ error: { kind: "invalid_request" }, ok: false });
    await expect(
      client.provision({ ...validCommand(), requestId: emptyUuid }),
    ).resolves.toEqual({ error: { kind: "invalid_request" }, ok: false });
    await expect(
      client.provision({ ...validCommand(), ownerEmail: "not-an-email" }),
    ).resolves.toEqual({ error: { kind: "invalid_request" }, ok: false });
    await expect(
      client.provision({
        ...validCommand(),
        groupName: "a".repeat(201),
      }),
    ).resolves.toEqual({ error: { kind: "invalid_request" }, ok: false });
    const commandWithExtraField = {
      ...validCommand(),
      actorUserId: "11111111-1111-4111-8111-111111111111",
    };
    await expect(client.provision(commandWithExtraField)).resolves.toEqual({
      error: { kind: "invalid_request" },
      ok: false,
    });
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it("fails closed for malformed published success bodies", async () => {
    server.use(
      http.get(handlerUrl(antiforgeryPath), () =>
        HttpResponse.json({ requestToken: "fresh-token" }),
      ),
      http.post(handlerUrl(provisioningPath), () =>
        HttpResponse.json(
          { ...provisionReceipt, invitationStatus: "delivered" },
          { status: 201 },
        ),
      ),
    );

    await expect(
      createPlatformOrganizationClient(createFetchSpy()).provision(
        validCommand(),
      ),
    ).resolves.toEqual({
      error: { kind: "protocol", mayHaveCommitted: true, status: 201 },
      ok: false,
    });
  });

  it("marks a success body missing contract fields as potentially committed", async () => {
    server.use(
      http.get(handlerUrl(antiforgeryPath), () =>
        HttpResponse.json({ requestToken: "fresh-token" }),
      ),
      http.post(handlerUrl(provisioningPath), () =>
        HttpResponse.json(
          { groupId: provisionReceipt.groupId },
          { status: 200 },
        ),
      ),
    );

    await expect(
      createPlatformOrganizationClient(createFetchSpy()).provision(
        validCommand(),
      ),
    ).resolves.toEqual({
      error: { kind: "protocol", mayHaveCommitted: true, status: 200 },
      ok: false,
    });
  });

  it.each([
    [401, "unauthorized"],
    [429, "rate_limited"],
  ] as const)("normalizes an empty %s response as %s", async (status, kind) => {
    server.use(
      http.get(handlerUrl(antiforgeryPath), () =>
        HttpResponse.json({ requestToken: "fresh-token" }),
      ),
      http.post(
        handlerUrl(provisioningPath),
        () => new HttpResponse(null, { status }),
      ),
    );

    await expect(
      createPlatformOrganizationClient(createFetchSpy()).provision(
        validCommand(),
      ),
    ).resolves.toEqual({ error: { kind, status }, ok: false });
  });

  it.each([
    ["platform_operator_required", "platform_operator_required"],
    ["mfa_assurance_required", "mfa_assurance_required"],
  ] as const)("normalizes the documented 403 code %s", async (code, kind) => {
    server.use(
      http.get(handlerUrl(antiforgeryPath), () =>
        HttpResponse.json({ requestToken: "fresh-token" }),
      ),
      http.post(handlerUrl(provisioningPath), () =>
        HttpResponse.json(
          { code, detail: "must not reach the UI" },
          { status: 403, headers: { "X-Correlation-ID": correlationId } },
        ),
      ),
    );

    const result = await createPlatformOrganizationClient(
      createFetchSpy(),
    ).provision(validCommand());

    expect(result).toEqual({
      error: { correlationId, kind, status: 403 },
      ok: false,
    });
    expect(JSON.stringify(result)).not.toContain("must not reach the UI");
  });

  it("fails closed for an undocumented 403 code", async () => {
    server.use(
      http.get(handlerUrl(antiforgeryPath), () =>
        HttpResponse.json({ requestToken: "fresh-token" }),
      ),
      http.post(handlerUrl(provisioningPath), () =>
        HttpResponse.json({ code: "teapot" }, { status: 403 }),
      ),
    );

    await expect(
      createPlatformOrganizationClient(createFetchSpy()).provision(
        validCommand(),
      ),
    ).resolves.toEqual({
      error: { kind: "protocol", status: 403 },
      ok: false,
    });
  });

  it.each([
    ["idempotency_conflict", "idempotency_conflict"],
    ["organization_conflict", "organization_conflict"],
  ] as const)("normalizes the documented 409 code %s", async (code, kind) => {
    server.use(
      http.get(handlerUrl(antiforgeryPath), () =>
        HttpResponse.json({ requestToken: "fresh-token" }),
      ),
      http.post(handlerUrl(provisioningPath), () =>
        HttpResponse.json(
          { code, detail: "must not reach the UI" },
          { status: 409, headers: { "X-Correlation-ID": correlationId } },
        ),
      ),
    );

    const result = await createPlatformOrganizationClient(
      createFetchSpy(),
    ).provision(validCommand());

    expect(result).toEqual({
      error: { correlationId, kind, status: 409 },
      ok: false,
    });
    expect(JSON.stringify(result)).not.toContain("must not reach the UI");
  });

  it("fails closed for an unknown conflict without leaking its body", async () => {
    const rawDetail = "unknown conflict internals";
    server.use(
      http.get(handlerUrl(antiforgeryPath), () =>
        HttpResponse.json({ requestToken: "fresh-token" }),
      ),
      http.post(handlerUrl(provisioningPath), () =>
        HttpResponse.json(
          { code: "unknown_conflict", detail: rawDetail },
          { status: 409 },
        ),
      ),
    );

    const result = await createPlatformOrganizationClient(
      createFetchSpy(),
    ).provision(validCommand());

    expect(result).toEqual({
      error: { kind: "protocol", status: 409 },
      ok: false,
    });
    expect(JSON.stringify(result)).not.toContain(rawDetail);
  });

  it.each([
    [500, "unavailable"],
    [503, "unavailable"],
  ] as const)(
    "marks unexpected provision server failures as potentially committed",
    async (status, kind) => {
      server.use(
        http.get(handlerUrl(antiforgeryPath), () =>
          HttpResponse.json({ requestToken: "fresh-token" }),
        ),
        http.post(handlerUrl(provisioningPath), () =>
          HttpResponse.json({ detail: "never expose" }, { status }),
        ),
      );

      const result = await createPlatformOrganizationClient(
        createFetchSpy(),
      ).provision(validCommand());

      expect(result).toEqual({
        error: { kind, mayHaveCommitted: true, status },
        ok: false,
      });
      expect(JSON.stringify(result)).not.toContain("never expose");
    },
  );

  it("maps a pre-provision abort definitively without starting antiforgery", async () => {
    const fetchSpy = createFetchSpy();
    const controller = new AbortController();
    controller.abort();

    await expect(
      createPlatformOrganizationClient(fetchSpy).provision(
        validCommand(),
        controller.signal,
      ),
    ).resolves.toEqual({ error: { kind: "aborted" }, ok: false });
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it("marks an abort after provision dispatch as potentially committed", async () => {
    const postAbort = Object.assign(new Error("private transport detail"), {
      name: "AbortError",
    });
    const fetchImplementation = vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input);
      if (path === antiforgeryPath)
        return new Response(JSON.stringify({ requestToken: "fresh-token" }));
      throw postAbort;
    });

    const result = await createPlatformOrganizationClient(
      fetchImplementation,
    ).provision(validCommand());

    expect(result).toEqual({
      error: { kind: "aborted", mayHaveCommitted: true },
      ok: false,
    });
    expect(JSON.stringify(result)).not.toContain("private transport detail");
  });

  it.each([200, 201])(
    "marks an abort while parsing a received %i provision response as potentially committed",
    async (status) => {
      const parseAbort = Object.assign(new Error("private body detail"), {
        name: "AbortError",
      });
      const receivedResponse = new Response(null, { status });
      vi.spyOn(receivedResponse, "json").mockRejectedValue(parseAbort);
      const fetchImplementation = vi.fn(async (input: RequestInfo | URL) =>
        String(input) === antiforgeryPath
          ? new Response(JSON.stringify({ requestToken: "fresh-token" }))
          : receivedResponse,
      );

      const result = await createPlatformOrganizationClient(
        fetchImplementation,
      ).provision(validCommand());

      expect(result).toEqual({
        error: { kind: "aborted", mayHaveCommitted: true, status },
        ok: false,
      });
      expect(JSON.stringify(result)).not.toContain("private body detail");
    },
  );

  it("marks an abort after a provision response before failure normalization as potentially committed", async () => {
    const controller = new AbortController();
    const fetchImplementation = vi.fn(async (input: RequestInfo | URL) => {
      if (String(input) === antiforgeryPath)
        return new Response(JSON.stringify({ requestToken: "fresh-token" }));

      controller.abort();
      return new Response(null, { status: 503 });
    });

    await expect(
      createPlatformOrganizationClient(fetchImplementation).provision(
        validCommand(),
        controller.signal,
      ),
    ).resolves.toEqual({
      error: { kind: "aborted", mayHaveCommitted: true },
      ok: false,
    });
  });

  it("marks a network failure after provision dispatch as potentially committed", async () => {
    const fetchImplementation = vi.fn(async (input: RequestInfo | URL) => {
      if (String(input) === antiforgeryPath)
        return new Response(JSON.stringify({ requestToken: "fresh-token" }));
      throw new Error("secret socket details");
    });

    const result = await createPlatformOrganizationClient(
      fetchImplementation,
    ).provision(validCommand());

    expect(result).toEqual({
      error: { kind: "network", mayHaveCommitted: true },
      ok: false,
    });
    expect(JSON.stringify(result)).not.toContain("secret");
  });

  it("maps network failures without exposing exception text", async () => {
    const client = createPlatformOrganizationClient(
      vi.fn().mockRejectedValue(new Error("secret socket details")),
    );

    const result = await client.provision(validCommand());

    expect(result).toEqual({ error: { kind: "network" }, ok: false });
    expect(JSON.stringify(result)).not.toContain("secret");
  });
});
