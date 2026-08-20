import { z } from "zod";
import {
  readCorrelationId,
  readErrorCode,
} from "../../shared/http/bff-response";
import type { BootstrapSnapshot } from "./session-contract";

const foundationBootstrapPath = "/foundation/bootstrap";

const bootstrapSnapshotSchema = z.object({
  user: z.object({
    id: z.uuid(),
    displayName: z.string(),
  }),
  companies: z.array(
    z.object({
      membershipId: z.uuid(),
      companyId: z.uuid(),
      companyName: z.string(),
      capabilities: z.array(
        z.object({
          name: z.string(),
          scopes: z.array(z.enum(["Self", "Team", "Unit", "Company", "Group"])),
        }),
      ),
    }),
  ),
  flags: z.record(z.string(), z.boolean()),
  platform: z.object({ isPlatformOperator: z.boolean() }),
});

const unavailableCodeSchema = z.enum([
  "identity_gate_unavailable",
  "audit_unavailable",
  "platform_operator_unavailable",
]);

export type FoundationBootstrapSnapshot = BootstrapSnapshot;
export type FoundationBootstrapUnavailableCode = z.infer<
  typeof unavailableCodeSchema
>;

export type FoundationBootstrapFailure = {
  kind:
    | "anonymous"
    | "unavailable"
    | "rate_limited"
    | "protocol"
    | "network"
    | "aborted";
  status?: number;
  code?: FoundationBootstrapUnavailableCode;
  correlationId?: string;
};

export type FoundationBootstrapLoadResult =
  | { ok: true; snapshot: FoundationBootstrapSnapshot; correlationId?: string }
  | { ok: false; error: FoundationBootstrapFailure };

export type FoundationBootstrapGateway = {
  load(signal?: AbortSignal): Promise<FoundationBootstrapLoadResult>;
};

/**
 * Carrega o snapshot BFF autenticado sem aceitar destino, credenciais ou
 * headers do consumidor.
 */
export function createFoundationBootstrapClient(
  fetchImplementation: typeof fetch = globalThis.fetch,
): FoundationBootstrapGateway {
  async function load(
    signal?: AbortSignal,
  ): Promise<FoundationBootstrapLoadResult> {
    if (signal?.aborted) {
      return failure("aborted");
    }

    let response: Response;
    try {
      response = await fetchImplementation(foundationBootstrapPath, {
        cache: "no-store",
        credentials: "same-origin",
        method: "GET",
        mode: "same-origin",
        redirect: "error",
        signal,
      });
    } catch (reason) {
      return failure(isAbort(reason, signal) ? "aborted" : "network");
    }

    const correlationId = readCorrelationId(response, { allowEmpty: true });
    if (response.status === 401) {
      return failure("anonymous", { correlationId, status: 401 });
    }
    if (response.status === 429) {
      return failure("rate_limited", { correlationId, status: 429 });
    }
    if (response.status === 503) {
      const code = await readErrorCode(response, unavailableCodeSchema);
      if (signal?.aborted) {
        return failure("aborted");
      }
      if (!code) {
        return failure("protocol", { correlationId, status: 503 });
      }

      return failure("unavailable", { correlationId, status: 503, code });
    }
    if (response.status !== 200) {
      return failure("protocol", { correlationId, status: response.status });
    }

    try {
      const parsed = bootstrapSnapshotSchema.safeParse(await response.json());
      if (!parsed.success) {
        return failure("protocol", { correlationId, status: response.status });
      }

      const snapshot: FoundationBootstrapSnapshot = parsed.data;
      return correlationId
        ? { ok: true, snapshot, correlationId }
        : { ok: true, snapshot };
    } catch (reason) {
      return failure(isAbort(reason, signal) ? "aborted" : "protocol", {
        correlationId,
        status: response.status,
      });
    }
  }

  return { load };
}

function failure(
  kind: FoundationBootstrapFailure["kind"],
  options: Omit<FoundationBootstrapFailure, "kind"> = {},
): FoundationBootstrapLoadResult {
  return {
    ok: false,
    error: {
      kind,
      ...(options.status === undefined ? {} : { status: options.status }),
      ...(options.code === undefined ? {} : { code: options.code }),
      ...(options.correlationId === undefined
        ? {}
        : { correlationId: options.correlationId }),
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
