import { z } from "zod";
import {
  bffFailure,
  fetchSameOriginBff,
  readAntiforgeryTokenBff,
  readCorrelationId,
  readErrorCode,
} from "../../shared/http/bff-response";

const antiforgeryPath = "/foundation/identity/browser-session/antiforgery";
const browserSessionPath = "/foundation/identity/browser-session";
const antiforgeryHeaderName = "X-CSRF-TOKEN";

const credentialsSchema = z
  .object({
    email: z.string().trim().min(1).max(320),
    password: z.string().min(1).max(1024),
  })
  .strict();
const documentedErrorCodeSchema = z.enum([
  "identity_gate_unavailable",
  "identity_provider_unavailable",
  "invalid_request",
]);

export type BrowserSessionCredentials = {
  email: string;
  password: string;
};

export type BrowserSessionErrorKind =
  | "invalid_request"
  | "unauthorized"
  | "forbidden"
  | "rate_limited"
  | "unavailable"
  | "protocol"
  | "network"
  | "aborted";

export type BrowserSessionErrorCode = z.infer<typeof documentedErrorCodeSchema>;

export type BrowserSessionFailure = {
  kind: BrowserSessionErrorKind;
  status?: number;
  code?: BrowserSessionErrorCode;
  correlationId?: string;
};

export type BrowserSessionFailureResult = {
  ok: false;
  error: BrowserSessionFailure;
};

export type BrowserSessionOperationResult =
  | { ok: true; correlationId?: string }
  | BrowserSessionFailureResult;

export type BrowserSessionSignOutFailureResult = BrowserSessionFailureResult & {
  stage: "antiforgery" | "session";
};

export type BrowserSessionSignOutResult =
  | { ok: true; correlationId?: string }
  | BrowserSessionSignOutFailureResult;

type FetchResult =
  | { response: Response }
  | { failure: BrowserSessionFailureResult };

type AntiforgeryResult =
  | { ok: true; requestToken: string }
  | BrowserSessionFailureResult;

/**
 * Cria chamadas BFF de sessão sem expor URL arbitrária, credenciais de provider
 * ou uma política implícita de repetição ao restante do browser.
 */
export function createBrowserSessionClient(
  fetchImplementation: typeof fetch = globalThis.fetch,
) {
  async function fetchSameOrigin(
    path: typeof antiforgeryPath | typeof browserSessionPath,
    init: RequestInit = {},
  ): Promise<FetchResult> {
    if (path !== antiforgeryPath && path !== browserSessionPath) {
      return { failure: failure("protocol") };
    }
    return fetchSameOriginBff(fetchImplementation, path, init, (kind) => ({
      failure: failure(kind),
    }));
  }

  async function getAntiforgery(
    signal?: AbortSignal,
  ): Promise<AntiforgeryResult> {
    return readAntiforgeryTokenBff(
      async (init) => {
        const result = await fetchSameOrigin(antiforgeryPath, init);
        return "response" in result
          ? { response: result.response }
          : result.failure;
      },
      (kind) => failure(kind),
      (response) => normalizeFailure(response, signal),
      signal,
    );
  }

  async function mutate(
    method: "POST" | "DELETE",
    requestToken: string,
    body?: string,
    signal?: AbortSignal,
  ): Promise<BrowserSessionOperationResult> {
    const result = await fetchSameOrigin(browserSessionPath, {
      body,
      headers: {
        ...(body ? { "Content-Type": "application/json" } : {}),
        Accept: "application/json",
        [antiforgeryHeaderName]: requestToken,
      },
      method,
      signal,
    });
    if ("failure" in result) {
      return result.failure;
    }

    if (result.response.status === 204) {
      return success(result.response);
    }

    return normalizeFailure(result.response, signal);
  }

  async function signIn(
    credentials: BrowserSessionCredentials,
    signal?: AbortSignal,
  ): Promise<BrowserSessionOperationResult> {
    const parsedCredentials = credentialsSchema.safeParse(credentials);
    if (!parsedCredentials.success) {
      return failure("invalid_request");
    }

    const antiforgery = await getAntiforgery(signal);
    if (!antiforgery.ok) {
      return antiforgery;
    }

    return mutate(
      "POST",
      antiforgery.requestToken,
      JSON.stringify(parsedCredentials.data),
      signal,
    );
  }

  async function signOut(
    signal?: AbortSignal,
  ): Promise<BrowserSessionSignOutResult> {
    const antiforgery = await getAntiforgery(signal);
    if (!antiforgery.ok) {
      return { ...antiforgery, stage: "antiforgery" };
    }

    const result = await mutate(
      "DELETE",
      antiforgery.requestToken,
      undefined,
      signal,
    );
    return result.ok ? result : { ...result, stage: "session" };
  }

  return { signIn, signOut };
}

function success(response: Response): BrowserSessionOperationResult {
  const correlationId = readCorrelationId(response, { allowEmpty: true });
  return correlationId ? { ok: true, correlationId } : { ok: true };
}

async function normalizeFailure(
  response: Response,
  signal?: AbortSignal,
): Promise<BrowserSessionFailureResult> {
  const correlationId = readCorrelationId(response, { allowEmpty: true });
  const code = await readErrorCode(response, documentedErrorCodeSchema);
  if (signal?.aborted) {
    return failure("aborted");
  }
  const options = { correlationId, status: response.status };

  switch (response.status) {
    case 400:
      return failure("invalid_request", {
        ...options,
        code: code === "invalid_request" ? code : undefined,
      });
    case 401:
      return failure("unauthorized", options);
    case 403:
      return failure("forbidden", options);
    case 429:
      return failure("rate_limited", options);
    case 503:
      return failure("unavailable", {
        ...options,
        code:
          code === "identity_gate_unavailable" ||
          code === "identity_provider_unavailable"
            ? code
            : undefined,
      });
    default:
      return failure("protocol", options);
  }
}

function failure(
  kind: BrowserSessionErrorKind,
  options: Omit<BrowserSessionFailure, "kind"> = {},
): BrowserSessionFailureResult {
  return bffFailure<BrowserSessionFailure>(kind, options);
}
