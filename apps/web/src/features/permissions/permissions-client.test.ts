import { HttpResponse, http } from "msw";
import { afterEach, describe, expect, it, vi } from "vitest";
import { server } from "../../test/msw/server";
import { createPermissionsClient } from "./permissions-client";

const antiforgeryPath = "/foundation/identity/browser-session/antiforgery";
const companyId = "01234567-89ab-cdef-0123-456789abcdef";
const requestId = "fedcba98-7654-3210-fedc-ba9876543210";
const membershipId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
const roleId = "11111111-2222-3333-4444-555555555555";
const roleAssignmentId = "99999999-8888-7777-6666-555555555555";
const correlationId = "12345678-1234-1234-1234-123456789abc";
const emptyUuid = "00000000-0000-0000-0000-000000000000";
const membersPath = `/foundation/companies/${companyId}/members`;
const rolesPath = `/foundation/companies/${companyId}/roles`;
const assignmentsPath = `/foundation/companies/${companyId}/role-assignments`;
const unassignmentsPath = `/foundation/companies/${companyId}/role-unassignments`;
const copiesPath = `/foundation/companies/${companyId}/permission-copies`;
const invitationsPath = `/foundation/companies/${companyId}/invitations`;
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

function validAssignment() {
  return { companyId, membershipId, requestId, roleId };
}

function validCopy() {
  return {
    companyId,
    requestId,
    sourceMembershipId: membershipId,
    targetMembershipId: "bbbbbbbb-cccc-dddd-eeee-ffffffffffff",
  };
}

function validInvite() {
  return {
    companyId,
    displayName: "Maria da Silva",
    email: "maria@example.com",
    requestId,
  };
}

function enableAntiforgery() {
  server.use(
    http.get(handlerUrl(antiforgeryPath), () =>
      HttpResponse.json({ requestToken: "fresh-token" }),
    ),
  );
}

afterEach(() => vi.restoreAllMocks());

describe("createPermissionsClient", () => {
  it("lists only the documented members through the fixed company path", async () => {
    server.use(
      http.get(handlerUrl(membersPath), ({ request }) => {
        expect(request.headers.get("Authorization")).toBeNull();
        return HttpResponse.json(
          [
            {
              capabilities: ["foundation.roles.assign"],
              displayName: "Maria da Silva",
              invitationStatus: null,
              membershipId,
              roles: [{ name: "Administrador", roleId }],
            },
          ],
          { headers: { "X-Correlation-ID": correlationId } },
        );
      }),
    );
    const fetchSpy = createFetchSpy();
    const client = createPermissionsClient(fetchSpy);

    await expect(client.listMembers(companyId)).resolves.toEqual({
      correlationId,
      items: [
        {
          capabilities: ["foundation.roles.assign"],
          displayName: "Maria da Silva",
          invitationStatus: null,
          membershipId,
          roles: [{ name: "Administrador", roleId }],
        },
      ],
      ok: true,
    });
    expect(fetchSpy.mock.calls.map(([path]) => String(path))).toEqual([
      membersPath,
    ]);
    expect(fetchSpy.mock.calls[0]?.[1]).toMatchObject({
      cache: "no-store",
      credentials: "same-origin",
      method: "GET",
      mode: "same-origin",
      redirect: "error",
    });
  });

  it("lists roles with grants without exposing anything beyond the contract", async () => {
    server.use(
      http.get(handlerUrl(rolesPath), () =>
        HttpResponse.json([
          {
            capabilities: ["foundation.roles.assign"],
            name: "Administrador",
            roleId,
          },
        ]),
      ),
    );

    await expect(
      createPermissionsClient(createFetchSpy()).listRoles(companyId),
    ).resolves.toEqual({
      items: [
        {
          capabilities: ["foundation.roles.assign"],
          name: "Administrador",
          roleId,
        },
      ],
      ok: true,
    });
  });

  it("fails closed for an undocumented member invitation status", async () => {
    server.use(
      http.get(handlerUrl(membersPath), () =>
        HttpResponse.json([
          {
            capabilities: [],
            displayName: "Maria",
            invitationStatus: "unknown_state",
            membershipId,
            roles: [],
          },
        ]),
      ),
    );

    await expect(
      createPermissionsClient(createFetchSpy()).listMembers(companyId),
    ).resolves.toEqual({ error: { kind: "protocol", status: 200 }, ok: false });
  });

  it("parses a member whose invitation was delivered and awaits acceptance", async () => {
    server.use(
      http.get(handlerUrl(membersPath), () =>
        HttpResponse.json([
          {
            capabilities: [],
            displayName: "Maria",
            invitationStatus: "pending_acceptance",
            membershipId,
            roles: [],
          },
        ]),
      ),
    );

    await expect(
      createPermissionsClient(createFetchSpy()).listMembers(companyId),
    ).resolves.toEqual({
      items: [
        {
          capabilities: [],
          displayName: "Maria",
          invitationStatus: "pending_acceptance",
          membershipId,
          roles: [],
        },
      ],
      ok: true,
    });
  });

  it("gets a fresh antiforgery token before a single strict assignment request", async () => {
    const requests: Request[] = [];
    enableAntiforgery();
    server.use(
      http.post(handlerUrl(assignmentsPath), async ({ request }) => {
        requests.push(request);
        expect(await request.json()).toEqual({
          membershipId,
          requestId,
          roleId,
        });
        expect(request.headers.get("X-CSRF-TOKEN")).toBe("fresh-token");
        expect(request.headers.get("Content-Type")).toBe("application/json");
        expect(request.headers.has("Authorization")).toBe(false);
        return HttpResponse.json(
          { roleAssignmentId },
          { headers: { "X-Correlation-ID": correlationId } },
        );
      }),
    );
    const fetchSpy = createFetchSpy();
    const client = createPermissionsClient(fetchSpy);

    await expect(client.assignRole(validAssignment())).resolves.toEqual({
      correlationId,
      ok: true,
    });
    expect(requests).toHaveLength(1);
    expect(fetchSpy.mock.calls.map(([path]) => String(path))).toEqual([
      antiforgeryPath,
      assignmentsPath,
    ]);
  });

  it("validates the unassignment receipt against the strict removed contract", async () => {
    enableAntiforgery();
    server.use(
      http.post(handlerUrl(unassignmentsPath), () =>
        HttpResponse.json({
          removed: true,
          roleAssignmentId,
        }),
      ),
    );

    await expect(
      createPermissionsClient(createFetchSpy()).unassignRole(validAssignment()),
    ).resolves.toEqual({ ok: true });
  });

  it("sends copy commands without leaking the company id into the body", async () => {
    enableAntiforgery();
    server.use(
      http.post(handlerUrl(copiesPath), async ({ request }) => {
        expect(await request.json()).toEqual({
          requestId,
          sourceMembershipId: validCopy().sourceMembershipId,
          targetMembershipId: validCopy().targetMembershipId,
        });
        return HttpResponse.json({
          appliedRoleIds: [roleId],
          targetMembershipId: validCopy().targetMembershipId,
        });
      }),
    );

    await expect(
      createPermissionsClient(createFetchSpy()).copyPermissions(validCopy()),
    ).resolves.toEqual({ ok: true });
  });

  it.each([
    [201, "created"],
    [200, "replay"],
  ] as const)(
    "normalizes an invitation %i response as %s without exposing identifiers",
    async (status, state) => {
      enableAntiforgery();
      server.use(
        http.post(handlerUrl(invitationsPath), async ({ request }) => {
          expect(await request.json()).toEqual({
            displayName: "Maria da Silva",
            email: "maria@example.com",
            requestId,
          });
          return HttpResponse.json(
            {
              invitationId: "77777777-7777-4777-8777-777777777777",
              invitationStatus: "pending_delivery",
              membershipId,
            },
            { status },
          );
        }),
      );

      await expect(
        createPermissionsClient(createFetchSpy()).inviteMember(validInvite()),
      ).resolves.toEqual({ ok: true, state });
    },
  );

  it("rejects malformed UUIDs and commands locally without HTTP", async () => {
    const fetchSpy = createFetchSpy();
    const client = createPermissionsClient(fetchSpy);

    await expect(client.listMembers("not-a-uuid")).resolves.toEqual({
      error: { kind: "invalid_request" },
      ok: false,
    });
    await expect(client.listRoles(emptyUuid)).resolves.toEqual({
      error: { kind: "invalid_request" },
      ok: false,
    });
    await expect(
      client.assignRole({ ...validAssignment(), roleId: "x" }),
    ).resolves.toEqual({ error: { kind: "invalid_request" }, ok: false });
    await expect(
      client.copyPermissions({ ...validCopy(), companyId: emptyUuid }),
    ).resolves.toEqual({ error: { kind: "invalid_request" }, ok: false });
    await expect(
      client.inviteMember({ ...validInvite(), email: "not-an-email" }),
    ).resolves.toEqual({ error: { kind: "invalid_request" }, ok: false });
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it.each([
    ["unassign", unassignmentsPath, "last_admin_guard"],
    ["copy", copiesPath, "last_admin_guard"],
    ["invite", invitationsPath, "member_conflict"],
    ["assign", assignmentsPath, "integrity_conflict"],
    ["invite", invitationsPath, "idempotency_conflict"],
  ] as const)(
    "normalizes the documented 409 code %s on %s without leaking its body",
    async (operation, path, code) => {
      enableAntiforgery();
      server.use(
        http.post(handlerUrl(path), () =>
          HttpResponse.json(
            { code, detail: "must not reach the UI" },
            { status: 409, headers: { "X-Correlation-ID": correlationId } },
          ),
        ),
      );
      const client = createPermissionsClient(createFetchSpy());

      const result =
        operation === "unassign"
          ? await client.unassignRole(validAssignment())
          : operation === "copy"
            ? await client.copyPermissions(validCopy())
            : operation === "invite"
              ? await client.inviteMember(validInvite())
              : await client.assignRole(validAssignment());

      expect(result).toEqual({
        error: { correlationId, kind: code, status: 409 },
        ok: false,
      });
      expect(JSON.stringify(result)).not.toContain("must not reach the UI");
    },
  );

  it("fails closed for an undocumented mutation conflict", async () => {
    enableAntiforgery();
    server.use(
      http.post(handlerUrl(assignmentsPath), () =>
        HttpResponse.json({ code: "unknown_conflict" }, { status: 409 }),
      ),
    );

    await expect(
      createPermissionsClient(createFetchSpy()).assignRole(validAssignment()),
    ).resolves.toEqual({
      error: { kind: "protocol", status: 409 },
      ok: false,
    });
  });

  it.each([
    [401, "unauthorized"],
    [403, "forbidden"],
    [404, "not_found"],
    [429, "rate_limited"],
  ] as const)("normalizes an empty %i response as %s", async (status, kind) => {
    server.use(
      http.get(
        handlerUrl(membersPath),
        () => new HttpResponse(null, { status }),
      ),
    );

    await expect(
      createPermissionsClient(createFetchSpy()).listMembers(companyId),
    ).resolves.toEqual({ error: { kind, status }, ok: false });
  });

  it("marks mutation unavailability as potentially committed but not reads", async () => {
    enableAntiforgery();
    server.use(
      http.get(handlerUrl(membersPath), () =>
        HttpResponse.json({ code: "audit_unavailable" }, { status: 503 }),
      ),
      http.post(handlerUrl(assignmentsPath), () =>
        HttpResponse.json({ code: "audit_unavailable" }, { status: 503 }),
      ),
    );
    const client = createPermissionsClient(createFetchSpy());

    await expect(client.listMembers(companyId)).resolves.toEqual({
      error: { kind: "unavailable", status: 503 },
      ok: false,
    });
    await expect(client.assignRole(validAssignment())).resolves.toEqual({
      error: { kind: "unavailable", mayHaveCommitted: true, status: 503 },
      ok: false,
    });
  });

  it("marks malformed published mutation success as potentially committed", async () => {
    enableAntiforgery();
    server.use(
      http.post(handlerUrl(assignmentsPath), () =>
        HttpResponse.json({ unexpected: true }, { status: 200 }),
      ),
    );

    await expect(
      createPermissionsClient(createFetchSpy()).assignRole(validAssignment()),
    ).resolves.toEqual({
      error: { kind: "protocol", mayHaveCommitted: true, status: 200 },
      ok: false,
    });
  });

  it("marks a transport failure after mutation dispatch as ambiguous", async () => {
    const fetchImplementation = vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input);
      if (path === antiforgeryPath)
        return new Response(JSON.stringify({ requestToken: "fresh-token" }));
      throw new Error("secret socket details");
    });

    const result = await createPermissionsClient(
      fetchImplementation,
    ).inviteMember(validInvite());

    expect(result).toEqual({
      error: { kind: "network", mayHaveCommitted: true },
      ok: false,
    });
    expect(JSON.stringify(result)).not.toContain("secret socket details");
  });

  it("keeps a pre-dispatch antiforgery failure definitive", async () => {
    server.use(
      http.get(
        handlerUrl(antiforgeryPath),
        () => new HttpResponse(null, { status: 503 }),
      ),
    );

    await expect(
      createPermissionsClient(createFetchSpy()).assignRole(validAssignment()),
    ).resolves.toEqual({
      error: { kind: "unavailable", status: 503 },
      ok: false,
    });
  });

  it("maps already-aborted signals without starting a request", async () => {
    const fetchSpy = createFetchSpy();
    const controller = new AbortController();
    controller.abort();

    await expect(
      createPermissionsClient(fetchSpy).listMembers(
        companyId,
        controller.signal,
      ),
    ).resolves.toEqual({ error: { kind: "aborted" }, ok: false });
    // Abort antes do disparo é definitivo: o comando nunca saiu do navegador.
    await expect(
      createPermissionsClient(fetchSpy).assignRole(
        validAssignment(),
        controller.signal,
      ),
    ).resolves.toEqual({ error: { kind: "aborted" }, ok: false });
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it("marks an abort while parsing a received invitation response as ambiguous", async () => {
    const parseAbort = Object.assign(new Error("private body detail"), {
      name: "AbortError",
    });
    const receivedResponse = new Response(null, { status: 201 });
    vi.spyOn(receivedResponse, "json").mockRejectedValue(parseAbort);
    const fetchImplementation = vi.fn(async (input: RequestInfo | URL) =>
      String(input) === antiforgeryPath
        ? new Response(JSON.stringify({ requestToken: "fresh-token" }))
        : receivedResponse,
    );

    const result = await createPermissionsClient(
      fetchImplementation,
    ).inviteMember(validInvite());

    expect(result).toEqual({
      error: { kind: "aborted", mayHaveCommitted: true, status: 201 },
      ok: false,
    });
    expect(JSON.stringify(result)).not.toContain("private body detail");
  });
});
