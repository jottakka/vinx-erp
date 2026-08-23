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
const directoryItemSchema = z
  .object({
    employeeId: canonicalUuidSchema,
    membershipId: canonicalUuidSchema,
    displayName: z.string(),
  })
  .strict();
const createCommandSchema = z
  .object({
    requestId: canonicalUuidSchema,
    companyId: canonicalUuidSchema,
    displayName: z.string().trim().min(1).max(200),
    email: z.string().trim().min(1).max(320).email(),
  })
  .strict();
const createResponseSchema = z
  .object({
    employeeId: canonicalUuidSchema,
    membershipId: canonicalUuidSchema,
    userId: canonicalUuidSchema,
  })
  .strict();
const conflictCodeSchema = z.enum([
  "employee_conflict",
  "idempotency_conflict",
]);

export type EmployeeDirectoryItem = z.infer<typeof directoryItemSchema>;
export type EmployeeDirectoryCreateCommand = z.infer<
  typeof createCommandSchema
>;

export type EmployeeDirectoryFailure = {
  kind:
    | "invalid_request"
    | "unauthorized"
    | "forbidden"
    | "employee_conflict"
    | "idempotency_conflict"
    | "rate_limited"
    | "unavailable"
    | "protocol"
    | "network"
    | "aborted";
  status?: number;
  correlationId?: string;
  mayHaveCommitted?: true;
};

type EmployeeDirectoryFailureResult = {
  ok: false;
  error: EmployeeDirectoryFailure;
};

export type EmployeeDirectoryListResult =
  | { ok: true; items: EmployeeDirectoryItem[]; correlationId?: string }
  | EmployeeDirectoryFailureResult;

export type EmployeeDirectoryCreateResult =
  | { ok: true; state: "created" | "replay"; correlationId?: string }
  | EmployeeDirectoryFailureResult;

export type EmployeeDirectoryGateway = {
  list(
    companyId: string,
    signal?: AbortSignal,
  ): Promise<EmployeeDirectoryListResult>;
  create(
    command: EmployeeDirectoryCreateCommand,
    signal?: AbortSignal,
  ): Promise<EmployeeDirectoryCreateResult>;
};

type FetchResult =
  | { response: Response }
  | { failure: EmployeeDirectoryFailureResult };
type Operation = "antiforgery" | "list" | "create";

/**
 * Cria chamadas BFF do diretório por empresa sem permitir URL, token ou retry
 * implícito fora deste limite de domínio.
 */
export function createEmployeeDirectoryClient(
  fetchImplementation: typeof fetch = globalThis.fetch,
): EmployeeDirectoryGateway {
  async function fetchSameOrigin(
    path: string,
    { mayHaveCommittedOnAbort = false, ...init }: SameOriginInit = {},
  ): Promise<FetchResult> {
    return fetchSameOriginBff(
      fetchImplementation,
      path,
      { ...init, mayHaveCommittedOnAbort },
      (kind, details) => ({
        failure: failure(
          kind,
          details.wasAborted && details.mayHaveCommittedOnAbort
            ? { mayHaveCommitted: true }
            : {},
        ),
      }),
    );
  }

  async function getAntiforgery(
    signal?: AbortSignal,
  ): Promise<
    { ok: true; requestToken: string } | EmployeeDirectoryFailureResult
  > {
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

  async function list(
    companyId: string,
    signal?: AbortSignal,
  ): Promise<EmployeeDirectoryListResult> {
    if (!isCanonicalUuid(companyId)) {
      return failure("invalid_request");
    }

    const result = await fetchSameOrigin(
      `/foundation/companies/${companyId}/employees`,
      { headers: { Accept: "application/json" }, method: "GET", signal },
    );
    if ("failure" in result) {
      return result.failure;
    }
    if (!result.response.ok) {
      return normalizeFailure(result.response, "list", signal);
    }

    const correlationId = readCorrelationId(result.response);
    try {
      const parsed = z
        .array(directoryItemSchema)
        .safeParse(await result.response.json());
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

  async function create(
    command: EmployeeDirectoryCreateCommand,
    signal?: AbortSignal,
  ): Promise<EmployeeDirectoryCreateResult> {
    const parsedCommand = createCommandSchema.safeParse(command);
    if (!parsedCommand.success) {
      return failure("invalid_request");
    }

    const antiforgery = await getAntiforgery(signal);
    if (!antiforgery.ok) {
      return antiforgery;
    }

    const result = await fetchSameOrigin(
      `/foundation/companies/${parsedCommand.data.companyId}/employees`,
      {
        body: JSON.stringify(parsedCommand.data),
        headers: {
          Accept: "application/json",
          "Content-Type": "application/json",
          [antiforgeryHeaderName]: antiforgery.requestToken,
        },
        method: "POST",
        mayHaveCommittedOnAbort: true,
        signal,
      },
    );
    if ("failure" in result) {
      return result.failure;
    }
    if (result.response.status !== 200 && result.response.status !== 201) {
      return normalizeFailure(result.response, "create", signal);
    }

    const correlationId = readCorrelationId(result.response);
    try {
      const parsed = createResponseSchema.safeParse(
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
        ? { ok: true, state, correlationId }
        : { ok: true, state };
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

  return { create, list };
}

async function normalizeFailure(
  response: Response,
  operation: Operation,
  signal?: AbortSignal,
): Promise<EmployeeDirectoryFailureResult> {
  if (signal?.aborted) {
    return failure(
      "aborted",
      operation === "create" ? { mayHaveCommitted: true } : {},
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
    case 409: {
      const code =
        operation === "create"
          ? await readErrorCode(response, conflictCodeSchema)
          : undefined;
      return code ? failure(code, options) : failure("protocol", options);
    }
    case 429:
      return failure("rate_limited", options);
    case 503:
      return failure(
        "unavailable",
        operation === "create"
          ? { ...options, mayHaveCommitted: true }
          : options,
      );
    default:
      if (operation === "create" && response.status >= 500) {
        return failure("unavailable", { ...options, mayHaveCommitted: true });
      }
      return failure("protocol", options);
  }
}

function failure(
  kind: EmployeeDirectoryFailure["kind"],
  options: Omit<EmployeeDirectoryFailure, "kind"> = {},
): EmployeeDirectoryFailureResult {
  return bffFailure<EmployeeDirectoryFailure>(kind, options);
}
