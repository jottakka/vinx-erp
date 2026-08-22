import { z } from "zod";

const correlationHeaderName = "X-Correlation-ID";
const canonicalUuidPattern =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const emptyUuid = "00000000-0000-0000-0000-000000000000";

type CanonicalUuidOptions = {
  allowEmpty?: boolean;
};

export function isCanonicalUuid(
  value: string,
  { allowEmpty = false }: CanonicalUuidOptions = {},
): boolean {
  return (
    canonicalUuidPattern.test(value) && (allowEmpty || value !== emptyUuid)
  );
}

export function readCorrelationId(
  response: Response,
  options?: CanonicalUuidOptions,
): string | undefined {
  const value = response.headers.get(correlationHeaderName);
  return value && isCanonicalUuid(value, options) ? value : undefined;
}

export async function readErrorCode<T extends z.ZodType>(
  response: Response,
  schema: T,
): Promise<z.infer<T> | undefined> {
  if (
    !response.headers
      .get("Content-Type")
      ?.toLowerCase()
      .startsWith("application/json")
  ) {
    return undefined;
  }

  try {
    const body = await response.json();
    const code =
      body && typeof body === "object"
        ? (body as { code?: unknown }).code
        : undefined;
    const parsed = schema.safeParse(code);
    return parsed.success ? parsed.data : undefined;
  } catch {
    return undefined;
  }
}
/**
 * Esquema compartilhado da resposta de antiforgery do BFF (o mesmo contrato
 * usado por todos os clients de mutação).
 */
export const bffAntiforgeryResponseSchema = z
  .object({ requestToken: z.string().min(1) })
  .strict();

/**
 * Detecta cancelamento: sinal abortado ou erro de rede com nome `AbortError`.
 */
export function isAbortError(
  reason: unknown,
  signal?: AbortSignal | null,
): boolean {
  return (
    signal?.aborted === true ||
    (reason instanceof Error && reason.name === "AbortError")
  );
}

export type SameOriginInit = RequestInit & {
  mayHaveCommittedOnAbort?: boolean;
};

export type TransportFailureFactory<R> = (
  kind: "aborted" | "network",
  details: { wasAborted: boolean; mayHaveCommittedOnAbort: boolean },
) => R;

/**
 * Transporte BFF seguro compartilhado: sem URL arbitrária (caminho fixo por
 * client), cache no-store, credenciais e modo same-origin, redirects proibidos
 * e mapeamento único de abort/erro de rede para a falha tipada do client.
 */
export async function fetchSameOriginBff<R>(
  fetchImplementation: typeof fetch,
  path: string,
  { mayHaveCommittedOnAbort = false, ...init }: SameOriginInit,
  fail: TransportFailureFactory<R>,
): Promise<{ response: Response } | R> {
  if (init.signal?.aborted) {
    return fail("aborted", {
      wasAborted: true,
      mayHaveCommittedOnAbort: false,
    });
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
    const wasAborted = isAbortError(reason, init.signal);
    return fail(wasAborted ? "aborted" : "network", {
      wasAborted,
      mayHaveCommittedOnAbort,
    });
  }
}

/**
 * Constrói o resultado de falha tipado do client, omitindo campos opcionais
 * indefinidos (mantém o formato enxuto do contrato em todos os clients).
 */
export function bffFailure<F extends { kind: string }>(
  kind: F["kind"],
  options: Omit<F, "kind"> = {} as Omit<F, "kind">,
): { ok: false; error: F } {
  const error: Record<string, unknown> = { kind };
  for (const [key, value] of Object.entries(options)) {
    if (value !== undefined) {
      error[key] = value;
    }
  }
  return { ok: false, error: error as unknown as F };
}

/**
 * Obtém o token de antiforgery do BFF com a mesma dança de fetch seguro,
 * normalização de falha do client e parse estrito do contrato — compartilhada
 * entre os clients de mutação.
 */
export async function readAntiforgeryTokenBff<R extends object>(
  request: (init: SameOriginInit) => Promise<{ response: Response } | R>,
  fail: (kind: "protocol" | "aborted", status?: number) => R,
  normalizeFailure: (response: Response) => Promise<R>,
  signal?: AbortSignal,
): Promise<{ ok: true; requestToken: string } | R> {
  const result = await request({
    headers: { Accept: "application/json" },
    method: "GET",
    signal,
  });
  if (!("response" in result)) {
    return result;
  }
  if (!result.response.ok) {
    return normalizeFailure(result.response);
  }

  try {
    const parsed = bffAntiforgeryResponseSchema.safeParse(
      await result.response.json(),
    );
    return parsed.success
      ? { ok: true, requestToken: parsed.data.requestToken }
      : fail("protocol", result.response.status);
  } catch (reason) {
    return fail(
      isAbortError(reason, signal) ? "aborted" : "protocol",
      result.response.status,
    );
  }
}
