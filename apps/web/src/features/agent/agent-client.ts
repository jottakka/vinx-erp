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

const agentRunStatusSchema = z.enum([
  "result",
  "needs_clarification",
  "denied",
  "requires_confirmation",
  "validation_error",
  "conflict",
]);

// Contrato tipado do endpoint: campos presentes e anuláveis, nunca ausentes.
const agentRunResponseSchema = z
  .object({
    status: agentRunStatusSchema,
    reasonCode: z.string().nullable(),
    text: z.string().nullable(),
    sources: z.array(agentRunSourceSchema).nullable(),
    correlationId: z.string(),
  })
  .strict();

// Envelope {"code"} documentado: antiforgery 400 e 5xx da fronteira.
const envelopeCodeSchema = z.enum([
  "invalid_request",
  "provider_not_configured",
  "agent_unavailable",
  "audit_unavailable",
  "agent_timeout",
]);

// reasonCodes estáveis da matriz da API (issue 138); desconhecido nunca é ecoado.
const knownReasonCodes = new Set([
  "ok",
  "tool_proposal_requires_confirmation",
  "capability_denied",
  "company_not_authorized",
  "company_required",
  "message_empty",
  "message_too_long",
  "request_id_invalid",
  "journey_invalid",
  "invalid_request",
  "idempotency_conflict",
  "integrity_conflict",
]);

export type AgentRunStatus = z.infer<typeof agentRunStatusSchema>;
export type AgentRunSource = z.infer<typeof agentRunSourceSchema>;
export type AgentRunEnvelopeCode = z.infer<typeof envelopeCodeSchema>;

export type AgentRunResponse = {
  status: AgentRunStatus;
  reasonCode: string | null;
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
  code?: AgentRunEnvelopeCode;
  correlationId?: string;
};

export type AgentRunSendResult =
  | { ok: true; run: AgentRunResponse }
  | { ok: false; failure: AgentRunFailure };

export type AgentRunInput = {
  message: string;
  companyId?: string;
  signal?: AbortSignal;
};

export type AgentRunGateway = {
  send(input: AgentRunInput): Promise<AgentRunSendResult>;
};

export function isKnownAgentReasonCode(value: string | null): value is string {
  return value !== null && knownReasonCodes.has(value);
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
    const token = await readAntiforgeryToken(input.signal);
    if (!("requestToken" in token)) {
      return { ok: false, failure: token };
    }

    const body = JSON.stringify({
      requestId: crypto.randomUUID(),
      journey: agentJourney,
      message: input.message,
      ...(input.companyId ? { companyId: input.companyId } : {}),
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

    if (response.status === 503 || response.status === 504) {
      const code = await readEnvelopeCode(response);
      if (!code) {
        return protocol(response.status, correlationId);
      }
      return {
        ok: false,
        failure:
          response.status === 504 && code === "agent_timeout"
            ? failure("timeout", {
                status: response.status,
                code,
                correlationId,
              })
            : failure("unavailable", {
                status: response.status,
                code,
                correlationId,
              }),
      };
    }

    if (![200, 400, 409].includes(response.status)) {
      return protocol(response.status, correlationId);
    }

    const body = await readBody(response);
    if (body === "unreadable") {
      return protocol(response.status, correlationId);
    }

    const typed = agentRunResponseSchema.safeParse(body);
    if (typed.success) {
      return {
        ok: true,
        run: correlationId ? { ...typed.data, correlationId } : typed.data,
      };
    }

    if (response.status === 400) {
      // 400 tem dois produtores: o endpoint (tipado, tratado acima) e o
      // middleware de antiforgery antes dele (envelope {"code"}).
      const code = parseEnvelopeCode(body);
      if (code === "invalid_request") {
        return {
          ok: false,
          failure: failure("antiforgery", { status: 400, code, correlationId }),
        };
      }
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

  async function readEnvelopeCode(
    response: Response,
  ): Promise<AgentRunEnvelopeCode | null> {
    const body = await readBody(response);
    return body === "unreadable" ? null : parseEnvelopeCode(body);
  }

  function parseEnvelopeCode(body: unknown): AgentRunEnvelopeCode | null {
    if (!body || typeof body !== "object") {
      return null;
    }
    const parsed = envelopeCodeSchema.safeParse(
      (body as { code?: unknown }).code,
    );
    return parsed.success ? parsed.data : null;
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
