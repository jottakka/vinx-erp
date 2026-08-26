import { z } from "zod";
import {
  bffFailure,
  fetchSameOriginBff,
  isAbortError,
  isCanonicalUuid,
  readAntiforgeryTokenBff,
  readCorrelationId,
  readErrorCode,
  type SameOriginInit,
} from "../../shared/http/bff-response";

const antiforgeryPath = "/foundation/identity/browser-session/antiforgery";
const antiforgeryHeaderName = "X-CSRF-TOKEN";

const canonicalUuidSchema = z
  .string()
  .refine((value) => isCanonicalUuid(value));

const invitationStatusSchema = z.enum([
  "pending_delivery",
  "pending_provider",
  "provider_in_flight",
  "pending_acceptance",
  "provider_failed",
  "provider_ambiguous",
]);
const memberRoleSchema = z
  .object({
    roleId: canonicalUuidSchema,
    name: z.string().min(1),
  })
  .strict();
const memberSchema = z
  .object({
    membershipId: canonicalUuidSchema,
    displayName: z.string().min(1),
    roles: z.array(memberRoleSchema),
    capabilities: z.array(z.string()),
    invitationStatus: invitationStatusSchema.nullable(),
  })
  .strict();
const roleSchema = z
  .object({
    roleId: canonicalUuidSchema,
    name: z.string().min(1),
    capabilities: z.array(z.string()),
  })
  .strict();
const assignmentCommandSchema = z
  .object({
    requestId: canonicalUuidSchema,
    companyId: canonicalUuidSchema,
    membershipId: canonicalUuidSchema,
    roleId: canonicalUuidSchema,
  })
  .strict();
const copyCommandSchema = z
  .object({
    requestId: canonicalUuidSchema,
    companyId: canonicalUuidSchema,
    sourceMembershipId: canonicalUuidSchema,
    targetMembershipId: canonicalUuidSchema,
  })
  .strict();
const inviteCommandSchema = z
  .object({
    requestId: canonicalUuidSchema,
    companyId: canonicalUuidSchema,
    displayName: z.string().trim().min(1).max(200),
    email: z.string().trim().min(1).max(320).email(),
  })
  .strict();
const assignmentResponseSchema = z
  .object({ roleAssignmentId: canonicalUuidSchema })
  .strict();
const unassignmentResponseSchema = z
  .object({
    roleAssignmentId: canonicalUuidSchema,
    removed: z.literal(true),
  })
  .strict();
const copyResponseSchema = z
  .object({
    targetMembershipId: canonicalUuidSchema,
    appliedRoleIds: z.array(canonicalUuidSchema),
  })
  .strict();
const inviteResponseSchema = z
  .object({
    membershipId: canonicalUuidSchema,
    invitationId: canonicalUuidSchema,
    invitationStatus: z.literal("pending_delivery"),
  })
  .strict();

const conflictCodeSchemas = {
  assign: z.enum(["idempotency_conflict", "integrity_conflict"]),
  unassign: z.enum([
    "idempotency_conflict",
    "integrity_conflict",
    "last_admin_guard",
  ]),
  copy: z.enum(["idempotency_conflict", "last_admin_guard"]),
  invite: z.enum(["idempotency_conflict", "member_conflict"]),
} as const;

export type PermissionsInvitationStatus = z.infer<
  typeof invitationStatusSchema
>;
export type PermissionsMember = z.infer<typeof memberSchema>;
export type PermissionsRole = z.infer<typeof roleSchema>;
export type PermissionsAssignmentCommand = z.infer<
  typeof assignmentCommandSchema
>;
export type PermissionsCopyCommand = z.infer<typeof copyCommandSchema>;
export type PermissionsInviteCommand = z.infer<typeof inviteCommandSchema>;

export type PermissionsFailure = {
  kind:
    | "invalid_request"
    | "unauthorized"
    | "forbidden"
    | "not_found"
    | "last_admin_guard"
    | "idempotency_conflict"
    | "integrity_conflict"
    | "member_conflict"
    | "rate_limited"
    | "unavailable"
    | "protocol"
    | "network"
    | "aborted";
  status?: number;
  correlationId?: string;
  mayHaveCommitted?: true;
};

type PermissionsFailureResult = {
  ok: false;
  error: PermissionsFailure;
};

export type PermissionsMembersResult =
  | { ok: true; items: PermissionsMember[]; correlationId?: string }
  | PermissionsFailureResult;
export type PermissionsRolesResult =
  | { ok: true; items: PermissionsRole[]; correlationId?: string }
  | PermissionsFailureResult;
/**
 * O recibo das mutações expõe apenas o estado: os identificadores opacos do
 * body são validados contra o contrato, mas nunca deixam este módulo.
 */
export type PermissionsMutationResult =
  | { ok: true; correlationId?: string }
  | PermissionsFailureResult;
export type PermissionsInviteResult =
  | { ok: true; state: "created" | "replay"; correlationId?: string }
  | PermissionsFailureResult;

export type PermissionsGateway = {
  listMembers(
    companyId: string,
    signal?: AbortSignal,
  ): Promise<PermissionsMembersResult>;
  listRoles(
    companyId: string,
    signal?: AbortSignal,
  ): Promise<PermissionsRolesResult>;
  assignRole(
    command: PermissionsAssignmentCommand,
    signal?: AbortSignal,
  ): Promise<PermissionsMutationResult>;
  unassignRole(
    command: PermissionsAssignmentCommand,
    signal?: AbortSignal,
  ): Promise<PermissionsMutationResult>;
  copyPermissions(
    command: PermissionsCopyCommand,
    signal?: AbortSignal,
  ): Promise<PermissionsMutationResult>;
  inviteMember(
    command: PermissionsInviteCommand,
    signal?: AbortSignal,
  ): Promise<PermissionsInviteResult>;
};

type FetchResult =
  | { response: Response }
  | { failure: PermissionsFailureResult };
type MutationOperation = keyof typeof conflictCodeSchemas;
type Operation = "antiforgery" | "members" | "roles" | MutationOperation;

const mutationOperations: readonly Operation[] = [
  "assign",
  "unassign",
  "copy",
  "invite",
];

/**
 * Cria as chamadas BFF do diretório de permissões sem permitir URL, token ou
 * retry implícito fora deste limite de domínio.
 */
export function createPermissionsClient(
  fetchImplementation: typeof fetch = globalThis.fetch,
): PermissionsGateway {
  async function fetchSameOrigin(
    path: string,
    { mayHaveCommittedOnAbort = false, ...init }: SameOriginInit = {},
  ): Promise<FetchResult> {
    return fetchSameOriginBff(
      fetchImplementation,
      path,
      { ...init, mayHaveCommittedOnAbort },
      (kind, details) => ({
        // Falha de transporte depois do disparo é ambígua mesmo sem abort:
        // o comando pode ter chegado ao servidor.
        failure: failure(
          kind,
          details.mayHaveCommittedOnAbort ? { mayHaveCommitted: true } : {},
        ),
      }),
    );
  }

  async function getAntiforgery(
    signal?: AbortSignal,
  ): Promise<{ ok: true; requestToken: string } | PermissionsFailureResult> {
    return readAntiforgeryTokenBff(
      async (init) => {
        const result = await fetchSameOrigin(antiforgeryPath, init);
        return "response" in result
          ? { response: result.response }
          : result.failure;
      },
      (kind, status) => failure(kind, status === undefined ? {} : { status }),
      (response) => normalizeFailure(response, "antiforgery", signal),
      signal,
    );
  }

  async function listCollection<T>(
    operation: "members" | "roles",
    companyId: string,
    schema: z.ZodArray<z.ZodType<T>>,
    signal?: AbortSignal,
  ): Promise<
    { ok: true; items: T[]; correlationId?: string } | PermissionsFailureResult
  > {
    if (!isCanonicalUuid(companyId)) {
      return failure("invalid_request");
    }

    const result = await fetchSameOrigin(
      `/foundation/companies/${companyId}/${operation}`,
      { headers: { Accept: "application/json" }, method: "GET", signal },
    );
    if ("failure" in result) {
      return result.failure;
    }
    if (!result.response.ok) {
      return normalizeFailure(result.response, operation, signal);
    }

    const correlationId = readCorrelationId(result.response);
    try {
      const parsed = schema.safeParse(await result.response.json());
      if (!parsed.success) {
        return failure("protocol", {
          correlationId,
          status: result.response.status,
        });
      }

      return correlationId
        ? { ok: true, items: parsed.data, correlationId }
        : { ok: true, items: parsed.data };
    } catch (reason) {
      return failure(isAbortError(reason, signal) ? "aborted" : "protocol", {
        correlationId,
        status: result.response.status,
      });
    }
  }

  async function sendCommand(
    path: string,
    body: Record<string, unknown>,
    signal?: AbortSignal,
  ): Promise<FetchResult | { antiforgery: PermissionsFailureResult }> {
    const antiforgery = await getAntiforgery(signal);
    if (!antiforgery.ok) {
      return { antiforgery };
    }

    return fetchSameOrigin(path, {
      body: JSON.stringify(body),
      headers: {
        Accept: "application/json",
        "Content-Type": "application/json",
        [antiforgeryHeaderName]: antiforgery.requestToken,
      },
      method: "POST",
      mayHaveCommittedOnAbort: true,
      signal,
    });
  }

  async function readMutationReceipt(
    result: Awaited<ReturnType<typeof sendCommand>>,
    operation: MutationOperation,
    responseSchema: z.ZodType,
    {
      acceptCreated = false,
      signal,
    }: { acceptCreated?: boolean; signal?: AbortSignal } = {},
  ): Promise<PermissionsMutationResult | PermissionsInviteResult> {
    if ("antiforgery" in result) {
      return result.antiforgery;
    }
    if ("failure" in result) {
      return result.failure;
    }
    const validStatus = acceptCreated
      ? result.response.status === 200 || result.response.status === 201
      : result.response.status === 200;
    if (!validStatus) {
      return normalizeFailure(result.response, operation, signal);
    }

    const correlationId = readCorrelationId(result.response);
    try {
      const parsed = responseSchema.safeParse(await result.response.json());
      if (!parsed.success) {
        return failure("protocol", {
          correlationId,
          mayHaveCommitted: true,
          status: result.response.status,
        });
      }

      if (acceptCreated) {
        const state =
          result.response.status === 201 ? "created" : ("replay" as const);
        return correlationId
          ? { ok: true, state, correlationId }
          : { ok: true, state };
      }
      return correlationId ? { ok: true, correlationId } : { ok: true };
    } catch (reason) {
      return isAbortError(reason, signal)
        ? failure("aborted", {
            correlationId,
            mayHaveCommitted: true,
            status: result.response.status,
          })
        : failure("protocol", {
            correlationId,
            mayHaveCommitted: true,
            status: result.response.status,
          });
    }
  }

  async function assignment(
    operation: "assign" | "unassign",
    command: PermissionsAssignmentCommand,
    signal?: AbortSignal,
  ): Promise<PermissionsMutationResult> {
    const parsedCommand = assignmentCommandSchema.safeParse(command);
    if (!parsedCommand.success) {
      return failure("invalid_request");
    }
    const { companyId, ...body } = parsedCommand.data;
    const result = await sendCommand(
      `/foundation/companies/${companyId}/${operation === "assign" ? "role-assignments" : "role-unassignments"}`,
      body,
      signal,
    );
    return readMutationReceipt(
      result,
      operation,
      operation === "assign"
        ? assignmentResponseSchema
        : unassignmentResponseSchema,
      { signal },
    ) as Promise<PermissionsMutationResult>;
  }

  async function listMembers(
    companyId: string,
    signal?: AbortSignal,
  ): Promise<PermissionsMembersResult> {
    return listCollection("members", companyId, z.array(memberSchema), signal);
  }

  async function listRoles(
    companyId: string,
    signal?: AbortSignal,
  ): Promise<PermissionsRolesResult> {
    return listCollection("roles", companyId, z.array(roleSchema), signal);
  }

  async function assignRole(
    command: PermissionsAssignmentCommand,
    signal?: AbortSignal,
  ): Promise<PermissionsMutationResult> {
    return assignment("assign", command, signal);
  }

  async function unassignRole(
    command: PermissionsAssignmentCommand,
    signal?: AbortSignal,
  ): Promise<PermissionsMutationResult> {
    return assignment("unassign", command, signal);
  }

  async function copyPermissions(
    command: PermissionsCopyCommand,
    signal?: AbortSignal,
  ): Promise<PermissionsMutationResult> {
    const parsedCommand = copyCommandSchema.safeParse(command);
    if (!parsedCommand.success) {
      return failure("invalid_request");
    }
    const { companyId, ...body } = parsedCommand.data;
    const result = await sendCommand(
      `/foundation/companies/${companyId}/permission-copies`,
      body,
      signal,
    );
    return readMutationReceipt(result, "copy", copyResponseSchema, {
      signal,
    }) as Promise<PermissionsMutationResult>;
  }

  async function inviteMember(
    command: PermissionsInviteCommand,
    signal?: AbortSignal,
  ): Promise<PermissionsInviteResult> {
    const parsedCommand = inviteCommandSchema.safeParse(command);
    if (!parsedCommand.success) {
      return failure("invalid_request");
    }
    const { companyId, ...body } = parsedCommand.data;
    const result = await sendCommand(
      `/foundation/companies/${companyId}/invitations`,
      body,
      signal,
    );
    return readMutationReceipt(result, "invite", inviteResponseSchema, {
      acceptCreated: true,
      signal,
    }) as Promise<PermissionsInviteResult>;
  }

  return {
    assignRole,
    copyPermissions,
    inviteMember,
    listMembers,
    listRoles,
    unassignRole,
  };
}

async function normalizeFailure(
  response: Response,
  operation: Operation,
  signal?: AbortSignal,
): Promise<PermissionsFailureResult> {
  if (signal?.aborted) {
    return failure(
      "aborted",
      isMutationOperation(operation) ? { mayHaveCommitted: true } : {},
    );
  }

  const correlationId = readCorrelationId(response);
  const options = { correlationId, status: response.status };
  switch (response.status) {
    case 400:
      return failure("invalid_request", options);
    case 401:
      return failure("unauthorized", options);
    case 403:
      return failure("forbidden", options);
    case 404:
      return failure("not_found", options);
    case 409: {
      if (!isMutationOperation(operation)) {
        return failure("protocol", options);
      }
      const code = await readErrorCode(
        response,
        conflictCodeSchemas[operation],
      );
      return code ? failure(code, options) : failure("protocol", options);
    }
    case 429:
      return failure("rate_limited", options);
    case 503:
      return failure("unavailable", withCommitted(options, operation));
    default:
      if (isMutationOperation(operation) && response.status >= 500) {
        return failure("unavailable", withCommitted(options, operation));
      }
      return failure("protocol", options);
  }
}

function isMutationOperation(
  operation: Operation,
): operation is MutationOperation {
  return mutationOperations.includes(operation);
}

function withCommitted(
  options: { correlationId?: string; status: number },
  operation: Operation,
) {
  return isMutationOperation(operation)
    ? { ...options, mayHaveCommitted: true as const }
    : options;
}

function failure(
  kind: PermissionsFailure["kind"],
  options: Omit<PermissionsFailure, "kind"> = {},
): PermissionsFailureResult {
  return bffFailure<PermissionsFailure>(kind, options);
}
