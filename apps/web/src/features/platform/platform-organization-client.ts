import { z } from "zod";
import {
  isCanonicalUuid,
  readCorrelationId,
  readErrorCode,
} from "../../shared/http/bff-response";

const antiforgeryPath = "/foundation/identity/browser-session/antiforgery";
const provisioningPath = "/foundation/platform/organizations";
const antiforgeryHeaderName = "X-CSRF-TOKEN";

const canonicalUuidSchema = z
  .string()
  .refine((value) => isCanonicalUuid(value));
const provisionCommandSchema = z
  .object({
    requestId: canonicalUuidSchema,
    groupName: z.string().trim().min(1).max(200),
    companyName: z.string().trim().min(1).max(200),
    ownerDisplayName: z.string().trim().min(1).max(200),
    ownerEmail: z.string().trim().min(1).max(320).email(),
  })
  .strict();
const antiforgeryResponseSchema = z
  .object({ requestToken: z.string().min(1) })
  .strict();
const provisionResponseSchema = z
  .object({
    groupId: canonicalUuidSchema,
    companyId: canonicalUuidSchema,
    ownerUserId: canonicalUuidSchema,
    ownerMembershipId: canonicalUuidSchema,
    ownerInvitationId: canonicalUuidSchema,
    invitationStatus: z.enum(["pending_delivery", "pending_acceptance"]),
  })
  .strict();
const conflictCodeSchema = z.enum([
  "idempotency_conflict",
  "organization_conflict",
]);
const forbiddenCodeSchema = z.enum([
  "platform_operator_required",
  "mfa_assurance_required",
]);

export type PlatformOrganizationProvisionCommand = z.infer<
  typeof provisionCommandSchema
>;
export type PlatformOrganizationInvitationStatus = z.infer<
  typeof provisionResponseSchema
>["invitationStatus"];

export type PlatformOrganizationFailure = {
  kind:
    | "invalid_request"
    | "unauthorized"
    | "platform_operator_required"
    | "mfa_assurance_required"
    | "idempotency_conflict"
    | "organization_conflict"
    | "rate_limited"
    | "unavailable"
    | "protocol"
    | "network"
    | "aborted";
  status?: number;
  correlationId?: string;
  mayHaveCommitted?: true;
};

type PlatformOrganizationFailureResult = {
  ok: false;
  error: PlatformOrganizationFailure;
};

/**
 * O recibo expõe apenas estado allow-listed: os identificadores opacos do body
 * são validados contra o contrato, mas nunca deixam este módulo.
 */
export type PlatformOrganizationProvisionResult =
  | {
      ok: true;
      state: "created" | "replay";
      invitationStatus: PlatformOrganizationInvitationStatus;
      correlationId?: string;
    }
  | PlatformOrganizationFailureResult;

export type PlatformOrganizationGateway = {
  provision(
    command: PlatformOrganizationProvisionCommand,
    signal?: AbortSignal,
  ): Promise<PlatformOrganizationProvisionResult>;
};

type FetchResult =
  | { response: Response }
  | { failure: PlatformOrganizationFailureResult };
type SameOriginRequestInit = RequestInit & {
  mayHaveCommittedOnAbort?: boolean;
};
type Operation = "antiforgery" | "provision";

/**
 * Cria a chamada BFF de provisioning de organização sem permitir URL, token ou
 * retry implícito fora deste limite de domínio.
 */
export function createPlatformOrganizationClient(
  fetchImplementation: typeof fetch = globalThis.fetch,
): PlatformOrganizationGateway {
  async function fetchSameOrigin(
    path: string,
    { mayHaveCommittedOnAbort = false, ...init }: SameOriginRequestInit = {},
  ): Promise<FetchResult> {
    if (init.signal?.aborted) {
      return { failure: failure("aborted") };
    }

    try {
      return {
        response: await fetchImplementation(path, {
          ...init,
          cache: "no-store",
          credentials: "same-origin",
          mode: "same-origin",
          redirect: "error",
        }),
      };
    } catch (reason) {
      const aborted = isAbort(reason, init.signal);
      return {
        // Falha de transporte depois do disparo é ambígua mesmo sem abort:
        // o comando pode ter chegado ao servidor.
        failure: failure(aborted ? "aborted" : "network", {
          ...(mayHaveCommittedOnAbort ? { mayHaveCommitted: true } : {}),
        }),
      };
    }
  }

  async function getAntiforgery(
    signal?: AbortSignal,
  ): Promise<
    { ok: true; requestToken: string } | PlatformOrganizationFailureResult
  > {
    const result = await fetchSameOrigin(antiforgeryPath, {
      headers: { Accept: "application/json" },
      method: "GET",
      signal,
    });
    if ("failure" in result) {
      return result.failure;
    }
    if (!result.response.ok) {
      return normalizeFailure(result.response, "antiforgery", signal);
    }

    try {
      const parsed = antiforgeryResponseSchema.safeParse(
        await result.response.json(),
      );
      return parsed.success
        ? { ok: true, requestToken: parsed.data.requestToken }
        : failure("protocol", { status: result.response.status });
    } catch (reason) {
      return failure(isAbort(reason, signal) ? "aborted" : "protocol", {
        status: result.response.status,
      });
    }
  }

  async function provision(
    command: PlatformOrganizationProvisionCommand,
    signal?: AbortSignal,
  ): Promise<PlatformOrganizationProvisionResult> {
    const parsedCommand = provisionCommandSchema.safeParse(command);
    if (!parsedCommand.success) {
      return failure("invalid_request");
    }

    const antiforgery = await getAntiforgery(signal);
    if (!antiforgery.ok) {
      return antiforgery;
    }

    const result = await fetchSameOrigin(provisioningPath, {
      body: JSON.stringify(parsedCommand.data),
      headers: {
        Accept: "application/json",
        "Content-Type": "application/json",
        [antiforgeryHeaderName]: antiforgery.requestToken,
      },
      method: "POST",
      mayHaveCommittedOnAbort: true,
      signal,
    });
    if ("failure" in result) {
      return result.failure;
    }
    if (result.response.status !== 200 && result.response.status !== 201) {
      return normalizeFailure(result.response, "provision", signal);
    }

    const correlationId = readCorrelationId(result.response);
    try {
      const parsed = provisionResponseSchema.safeParse(
        await result.response.json(),
      );
      if (!parsed.success) {
        return failure("protocol", {
          correlationId,
          mayHaveCommitted: true,
          status: result.response.status,
        });
      }

      const state = result.response.status === 201 ? "created" : "replay";
      return correlationId
        ? {
            ok: true,
            state,
            invitationStatus: parsed.data.invitationStatus,
            correlationId,
          }
        : { ok: true, state, invitationStatus: parsed.data.invitationStatus };
    } catch (reason) {
      return isAbort(reason, signal)
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

  return { provision };
}

async function normalizeFailure(
  response: Response,
  operation: Operation,
  signal?: AbortSignal,
): Promise<PlatformOrganizationFailureResult> {
  if (signal?.aborted) {
    return failure(
      "aborted",
      operation === "provision" ? { mayHaveCommitted: true } : {},
    );
  }

  const correlationId = readCorrelationId(response);
  const options = { correlationId, status: response.status };
  switch (response.status) {
    case 400:
      return failure("invalid_request", options);
    case 401:
      return failure("unauthorized", options);
    case 403: {
      if (operation !== "provision") {
        return failure("protocol", options);
      }
      const code = await readErrorCode(response, forbiddenCodeSchema);
      return code ? failure(code, options) : failure("protocol", options);
    }
    case 409: {
      if (operation !== "provision") {
        return failure("protocol", options);
      }
      const code = await readErrorCode(response, conflictCodeSchema);
      return code ? failure(code, options) : failure("protocol", options);
    }
    case 429:
      return failure("rate_limited", options);
    case 503:
      return failure("unavailable", withCommitted(options, operation));
    default:
      if (operation === "provision" && response.status >= 500) {
        return failure("unavailable", withCommitted(options, operation));
      }
      return failure("protocol", options);
  }
}

function withCommitted(
  options: { correlationId?: string; status: number },
  operation: Operation,
) {
  return operation === "provision"
    ? { ...options, mayHaveCommitted: true as const }
    : options;
}

function failure(
  kind: PlatformOrganizationFailure["kind"],
  options: Omit<PlatformOrganizationFailure, "kind"> = {},
): PlatformOrganizationFailureResult {
  return {
    ok: false,
    error: {
      kind,
      ...(options.status === undefined ? {} : { status: options.status }),
      ...(options.correlationId === undefined
        ? {}
        : { correlationId: options.correlationId }),
      ...(options.mayHaveCommitted ? { mayHaveCommitted: true as const } : {}),
    },
  };
}

function isAbort(
  reason: unknown,
  signal: AbortSignal | null | undefined,
): boolean {
  return (
    signal?.aborted === true ||
    (reason instanceof Error && reason.name === "AbortError")
  );
}
