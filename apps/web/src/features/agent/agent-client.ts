import { z } from "zod";
import {
  bffFailure,
  fetchSameOriginBff,
  readAntiforgeryTokenBff,
  readCorrelationId,
  type SameOriginInit,
} from "../../shared/http/bff-response";

const agentRunsPath = "/agent/runs";
const antiforgeryPath = "/foundation/identity/browser-session/antiforgery";
const antiforgeryHeaderName = "X-CSRF-TOKEN";

/**
 * Jornada aprovada enviada pelo workspace do agente; o catálogo server-side
 * é a autoridade (perfil único `read_only_summarize_v1`).
 */
export const agentJourney = "read_only_summarize_v1";

const agentRunSourceSchema = z
  .object({ reference: z.string().min(1) })
  .strict();

// reasonCodes estáveis da matriz da API (issue 138); desconhecido é
// normalizado para null — nunca ecoado cru.
const knownReasonCodes = [
  "ok",
  "tool_proposal_requires_confirmation",
  "capability_denied",
  "company_not_authorized",
  "company_required",
  "no_evidence",
  "message_empty",
  "message_too_long",
  "request_id_invalid",
  "journey_invalid",
  "invalid_request",
  "idempotency_conflict",
  "integrity_conflict",
  "document_context_unavailable",
] as const;

export type AgentRunReasonCode = (typeof knownReasonCodes)[number];

const knownReasonCodeSet = new Set<string>(knownReasonCodes);

const normalizedReasonCode = z
  .string()
  .nullable()
  .transform((value): AgentRunReasonCode | null =>
    value !== null && knownReasonCodeSet.has(value)
      ? (value as AgentRunReasonCode)
      : null,
  );

// Campos do corpo tipado: presentes e anuláveis, nunca ausentes; cada
// status HTTP aceita SOMENTE os status de contrato que ele produz.
const typedResponseFields = {
  reasonCode: normalizedReasonCode,
  text: z.string().nullable(),
  sources: z.array(agentRunSourceSchema).nullable(),
  correlationId: z.uuid(),
} as const;

const okStatusSchema = z.enum([
  "result",
  "needs_clarification",
  "denied",
  "requires_confirmation",
]);

const runOkSchema = z
  .object({ status: okStatusSchema, ...typedResponseFields })
  .strict();
const validationSchema = z
  .object({ status: z.literal("validation_error"), ...typedResponseFields })
  .strict();
const conflictSchema = z
  .object({ status: z.literal("conflict"), ...typedResponseFields })
  .strict();

// Envelopes {"code"} por produtor: antiforgery 400 e cada 5xx da fronteira
// com a sua allow-list exata; objetos estritos (campos extras rejeitados).
const antiforgeryEnvelopeSchema = z
  .object({ code: z.literal("invalid_request") })
  .strict();
const unavailableEnvelopeSchema = z
  .object({
    code: z.enum([
      "provider_not_configured",
      "agent_unavailable",
      "audit_unavailable",
    ]),
  })
  .strict();
const timeoutEnvelopeSchema = z
  .object({ code: z.literal("agent_timeout") })
  .strict();

export type AgentRunStatus =
  | z.infer<typeof okStatusSchema>
  | "validation_error"
  | "conflict";
export type AgentRunSource = z.infer<typeof agentRunSourceSchema>;

export type AgentRunResponse = {
  status: AgentRunStatus;
  reasonCode: AgentRunReasonCode | null;
  text: string | null;
  sources: readonly AgentRunSource[] | null;
  correlationId: string;
};

export type AgentRunFailureKind =
  | "antiforgery"
  | "unauthorized"
  | "rate_limited"
  | "unavailable"
  | "timeout"
  | "network"
  | "aborted"
  | "protocol";

export type AgentRunFailure = {
  kind: AgentRunFailureKind;
  status?: number;
  code?: string;
  correlationId?: string;
};

export type AgentRunSendResult =
  | { ok: true; run: AgentRunResponse }
  | { ok: false; failure: AgentRunFailure };

export type AgentRunInput = {
  attachments?: readonly string[];
  message: string;
  companyId?: string;
  signal?: AbortSignal;
};

export type AgentRunGateway = {
  send(input: AgentRunInput): Promise<AgentRunSendResult>;
};

export function isKnownAgentReasonCode(value: string | null): value is string {
  return value !== null && knownReasonCodeSet.has(value);
}

/**
 * Client de POST /agent/runs: same-origin, antiforgery obrigatório,
 * requestId novo por envio e falhas sempre tipadas — nenhum body, prompt ou
 * erro cru vaza em exceção, log ou UI.
 */
export function createAgentRunClient(
  fetchImplementation: typeof fetch = globalThis.fetch,
): AgentRunGateway {
  async function requestBff(
    path: string,
    init: SameOriginInit = {},
  ): Promise<{ response: Response } | AgentRunFailure> {
    const result = await fetchSameOriginBff(
      fetchImplementation,
      path,
      init,
      (kind) => failure(kind),
    );
    return "response" in result ? { response: result.response } : result;
  }

  async function readAntiforgeryToken(
    signal?: AbortSignal,
  ): Promise<{ ok: true; requestToken: string } | AgentRunFailure> {
    return readAntiforgeryTokenBff(
      async (init) => {
        const result = await requestBff(antiforgeryPath, init);
        return "response" in result ? { response: result.response } : result;
      },
      (kind, status) =>
        status === undefined ? failure(kind) : failure(kind, { status }),
      async (response) =>
        normalizeStatusFailure(response) ??
        failure("protocol", { status: response.status }),
      signal,
    );
  }

  async function send(input: AgentRunInput): Promise<AgentRunSendResult> {
    const attachments = input.attachments?.slice();
    const token = await readAntiforgeryToken(input.signal);
    if (!("requestToken" in token)) {
      return { ok: false, failure: token };
    }

    const body = JSON.stringify({
      requestId: crypto.randomUUID(),
      journey: agentJourney,
      message: input.message,
      ...(input.companyId ? { companyId: input.companyId } : {}),
      ...(attachments?.length ? { attachments } : {}),
    });

    const result = await requestBff(agentRunsPath, {
      body,
      headers: {
        Accept: "application/json",
        "Content-Type": "application/json",
        [antiforgeryHeaderName]: token.requestToken,
      },
      mayHaveCommittedOnAbort: true,
      method: "POST",
      signal: input.signal,
    });
    if (!("response" in result)) {
      return { ok: false, failure: result };
    }

    return normalizeRunResponse(result.response);
  }

  // O corpo da resposta é lido UMA vez e reusado pelos parsers; nenhum
  // conteúdo dele vaza em exceção, log ou resultado.
  async function normalizeRunResponse(
    response: Response,
  ): Promise<AgentRunSendResult> {
    const correlationId = readCorrelationId(response);
    const statusFailure = normalizeStatusFailure(response);
    if (statusFailure) {
      return { ok: false, failure: statusFailure };
    }

    if (response.status === 400) {
      const body = await readBody(response);
      if (body === "unreadable") {
        return protocol(response.status, correlationId);
      }
      const typed = validationSchema.safeParse(body);
      if (typed.success) {
        return ok(typed.data, correlationId);
      }
      // 400 tem dois produtores: o endpoint (tipado, tratado acima) e o
      // middleware de antiforgery antes dele (envelope {"code"}).
      if (antiforgeryEnvelopeSchema.safeParse(body).success) {
        return {
          ok: false,
          failure: failure("antiforgery", {
            status: 400,
            code: "invalid_request",
            correlationId,
          }),
        };
      }
      return protocol(response.status, correlationId);
    }

    if (response.status === 503 || response.status === 504) {
      const body = await readBody(response);
      if (body === "unreadable") {
        return protocol(response.status, correlationId);
      }
      const code =
        response.status === 503
          ? envelopeCode(body, unavailableEnvelopeSchema)
          : envelopeCode(body, timeoutEnvelopeSchema);
      if (!code) {
        return protocol(response.status, correlationId);
      }
      const kind = response.status === 504 ? "timeout" : "unavailable";
      return {
        ok: false,
        failure: failure(kind, {
          status: response.status,
          code,
          correlationId,
        }),
      };
    }

    if (response.status === 200 || response.status === 409) {
      const body = await readBody(response);
      if (body === "unreadable") {
        return protocol(response.status, correlationId);
      }
      const schema = response.status === 200 ? runOkSchema : conflictSchema;
      const typed = schema.safeParse(body);
      return typed.success
        ? ok(typed.data, correlationId)
        : protocol(response.status, correlationId);
    }

    return protocol(response.status, correlationId);
  }

  function normalizeStatusFailure(response: Response): AgentRunFailure | null {
    if (response.status === 401) {
      return failure("unauthorized", {
        status: 401,
        correlationId: readCorrelationId(response),
      });
    }
    if (response.status === 429) {
      return failure("rate_limited", {
        status: 429,
        correlationId: readCorrelationId(response),
      });
    }
    return null;
  }

  function envelopeCode(
    body: unknown,
    schema: z.ZodType<{ code: string }>,
  ): string | null {
    const parsed = schema.safeParse(body);
    return parsed.success ? parsed.data.code : null;
  }

  async function readBody(response: Response): Promise<unknown | "unreadable"> {
    if (
      !response.headers
        .get("Content-Type")
        ?.toLowerCase()
        .startsWith("application/json")
    ) {
      return "unreadable";
    }
    try {
      return await response.json();
    } catch {
      return "unreadable";
    }
  }

  function ok(
    run: AgentRunResponse,
    correlationId: string | undefined,
  ): AgentRunSendResult {
    return { ok: true, run: correlationId ? { ...run, correlationId } : run };
  }

  function protocol(
    status: number,
    correlationId?: string,
  ): AgentRunSendResult {
    return {
      ok: false,
      failure: failure("protocol", { status, correlationId }),
    };
  }

  function failure(
    kind: AgentRunFailureKind,
    options: Omit<AgentRunFailure, "kind"> = {},
  ): AgentRunFailure {
    return bffFailure<AgentRunFailure>(kind, options).error;
  }

  return { send };
}
