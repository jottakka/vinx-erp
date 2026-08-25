import { z } from "zod";
import {
  fetchSameOriginBff,
  readAntiforgeryTokenBff,
  type SameOriginInit,
} from "../../shared/http/bff-response";

const antiforgeryPath = "/foundation/identity/browser-session/antiforgery";
const confirmationPath = "/foundation/files/confirmations";
const intentionPath = "/foundation/files/upload-intentions";
const antiforgeryHeaderName = "X-CSRF-TOKEN";
const maxFileSizeBytes = 26_214_400;
const retentionMilliseconds = 30 * 24 * 60 * 60 * 1000;

const allowedMimeTypes = new Set([
  "application/pdf",
  "image/jpeg",
  "image/png",
]);

const intentionResponseSchema = z
  .object({
    fileId: z.uuid(),
    processingState: z.literal("quarantined"),
    uploadUrl: z.string().min(1),
  })
  .strict();

const confirmationResponseSchema = z
  .object({
    fileId: z.uuid(),
    processingState: z.literal("confirmed"),
  })
  .strict();

export type AgentAttachmentUploadStage =
  | "validation"
  | "intention"
  | "storage"
  | "confirmation";

export type AgentAttachmentUploadFailure = {
  stage: AgentAttachmentUploadStage;
  kind: "validation" | "aborted" | "network" | "protocol";
  status?: number;
};

export type UploadedAgentAttachment = {
  fileId: string;
  fileName: string;
  mimeType: string;
  sizeBytes: number;
};

export type AgentAttachmentUploadResult =
  | { ok: true; attachment: UploadedAgentAttachment }
  | { ok: false; error: AgentAttachmentUploadFailure };

export type AgentAttachmentUploadInput = {
  companyId: string;
  file: File;
  signal: AbortSignal;
};

export type AgentAttachmentUploadGateway = {
  upload(
    input: AgentAttachmentUploadInput,
  ): Promise<AgentAttachmentUploadResult>;
};

export type AgentAttachmentClientOptions = {
  fetchImplementation?: typeof fetch;
  cryptoImplementation?: Pick<Crypto, "randomUUID"> & {
    subtle: Pick<SubtleCrypto, "digest">;
  };
  now?: () => Date;
};

/**
 * Envia um anexo do chat pelo ciclo intention → storage → confirmation. A URL
 * temporária de storage nunca sai desta função, nem entra em erro ou estado.
 */
export function createAgentAttachmentClient({
  fetchImplementation = globalThis.fetch,
  cryptoImplementation = globalThis.crypto,
  now = () => new Date(),
}: AgentAttachmentClientOptions = {}): AgentAttachmentUploadGateway {
  async function requestBff(
    stage: AgentAttachmentUploadStage,
    path: string,
    init: SameOriginInit = {},
  ): Promise<{ response: Response } | AgentAttachmentUploadFailure> {
    const result = await fetchSameOriginBff(
      fetchImplementation,
      path,
      init,
      (kind) => failure(stage, kind),
    );
    if (init.signal?.aborted) {
      return failure(stage, "aborted");
    }
    return "response" in result ? { response: result.response } : result;
  }

  async function readAntiforgeryToken(
    stage: AgentAttachmentUploadStage,
    signal: AbortSignal,
  ): Promise<{ requestToken: string } | AgentAttachmentUploadFailure> {
    const token = await readAntiforgeryTokenBff(
      async (init) => requestBff(stage, antiforgeryPath, init),
      (kind, status) => failure(stage, kind, status),
      async (response) => failure(stage, "protocol", response.status),
      signal,
    );
    if (signal.aborted) {
      return failure(stage, "aborted");
    }
    return token;
  }

  async function postBff(
    stage: "intention" | "confirmation",
    path: string,
    body: string,
    signal: AbortSignal,
  ): Promise<{ response: Response } | AgentAttachmentUploadFailure> {
    const token = await readAntiforgeryToken(stage, signal);
    if (signal.aborted) {
      return failure(stage, "aborted");
    }
    if (!("requestToken" in token)) {
      return token;
    }

    return requestBff(stage, path, {
      body,
      headers: {
        Accept: "application/json",
        "Content-Type": "application/json",
        [antiforgeryHeaderName]: token.requestToken,
      },
      mayHaveCommittedOnAbort: true,
      method: "POST",
      signal,
    });
  }

  async function upload({
    companyId,
    file,
    signal,
  }: AgentAttachmentUploadInput): Promise<AgentAttachmentUploadResult> {
    if (signal.aborted) {
      return fail("validation", "aborted");
    }
    if (
      !file.name.trim() ||
      !allowedMimeTypes.has(file.type) ||
      file.size < 1 ||
      file.size > maxFileSizeBytes
    ) {
      return fail("validation", "validation");
    }

    let bytes: ArrayBuffer;
    try {
      bytes = await file.arrayBuffer();
    } catch {
      return fail("validation", signal.aborted ? "aborted" : "protocol");
    }
    if (signal.aborted) {
      return fail("validation", "aborted");
    }

    let checksum: string;
    try {
      checksum = `sha256:${toLowerHex(
        await cryptoImplementation.subtle.digest("SHA-256", bytes),
      )}`;
    } catch {
      return fail("validation", signal.aborted ? "aborted" : "protocol");
    }
    if (signal.aborted) {
      return fail("validation", "aborted");
    }

    const intention = await postBff(
      "intention",
      intentionPath,
      JSON.stringify({
        requestId: cryptoImplementation.randomUUID(),
        companyId,
        fileName: file.name,
        mimeType: file.type,
        sizeBytes: file.size,
        checksum,
        classification: "agentic_attachment",
        origin: "web-agent-chat",
        retentionUntil: new Date(
          now().getTime() + retentionMilliseconds,
        ).toISOString(),
      }),
      signal,
    );
    if (signal.aborted) {
      return fail("intention", "aborted");
    }
    if (!("response" in intention)) {
      return { ok: false, error: intention };
    }

    const intent = await parseIntention(intention.response, signal);
    if (signal.aborted) {
      return fail("intention", "aborted");
    }
    if (!intent.ok) {
      return intent;
    }
    if (signal.aborted) {
      return fail("storage", "aborted");
    }

    const storage = await putStorage(
      intent.uploadUrl,
      file.type,
      bytes,
      signal,
    );
    if (signal.aborted) {
      return fail("storage", "aborted");
    }
    if (!storage.ok) {
      return storage;
    }

    const confirmation = await postBff(
      "confirmation",
      confirmationPath,
      JSON.stringify({
        requestId: cryptoImplementation.randomUUID(),
        companyId,
        fileId: intent.fileId,
      }),
      signal,
    );
    if (signal.aborted) {
      return fail("confirmation", "aborted");
    }
    if (!("response" in confirmation)) {
      return { ok: false, error: confirmation };
    }

    const confirmed = await parseConfirmation(
      confirmation.response,
      intent.fileId,
      signal,
    );
    if (signal.aborted) {
      return fail("confirmation", "aborted");
    }
    if (!confirmed.ok) {
      return confirmed;
    }

    return {
      ok: true,
      attachment: {
        fileId: intent.fileId,
        fileName: file.name,
        mimeType: file.type,
        sizeBytes: file.size,
      },
    };
  }

  async function parseIntention(
    response: Response,
    signal: AbortSignal,
  ): Promise<
    | { ok: true; fileId: string; uploadUrl: string }
    | { ok: false; error: AgentAttachmentUploadFailure }
  > {
    if (signal.aborted) {
      return fail("intention", "aborted");
    }
    if (response.status !== 200) {
      return fail("intention", "protocol", response.status);
    }
    const body = await readJson(response);
    if (signal.aborted) {
      return fail("intention", "aborted");
    }
    const parsed = intentionResponseSchema.safeParse(body);
    if (!parsed.success || !isAllowedUploadUrl(parsed.data.uploadUrl)) {
      return fail("intention", "protocol", response.status);
    }
    return {
      ok: true,
      fileId: parsed.data.fileId,
      uploadUrl: parsed.data.uploadUrl,
    };
  }

  async function putStorage(
    uploadUrl: string,
    mimeType: string,
    bytes: ArrayBuffer,
    signal: AbortSignal,
  ): Promise<
    { ok: true } | { ok: false; error: AgentAttachmentUploadFailure }
  > {
    try {
      const response = await fetchImplementation(uploadUrl, {
        body: bytes,
        credentials: "omit",
        headers: { "Content-Type": mimeType },
        method: "PUT",
        redirect: "error",
        signal,
      });
      if (signal.aborted) {
        return fail("storage", "aborted");
      }
      return response.status >= 200 && response.status < 300
        ? { ok: true }
        : fail("storage", "protocol");
    } catch {
      return fail("storage", signal.aborted ? "aborted" : "network");
    }
  }

  async function parseConfirmation(
    response: Response,
    intendedFileId: string,
    signal: AbortSignal,
  ): Promise<
    { ok: true } | { ok: false; error: AgentAttachmentUploadFailure }
  > {
    if (signal.aborted) {
      return fail("confirmation", "aborted");
    }
    if (response.status !== 200) {
      return fail("confirmation", "protocol", response.status);
    }
    const body = await readJson(response);
    if (signal.aborted) {
      return fail("confirmation", "aborted");
    }
    const parsed = confirmationResponseSchema.safeParse(body);
    return parsed.success && parsed.data.fileId === intendedFileId
      ? { ok: true }
      : fail("confirmation", "protocol", response.status);
  }

  return { upload };
}

async function readJson(response: Response): Promise<unknown> {
  if (
    !response.headers
      .get("Content-Type")
      ?.toLowerCase()
      .startsWith("application/json")
  ) {
    return undefined;
  }
  try {
    return await response.json();
  } catch {
    return undefined;
  }
}

function isAllowedUploadUrl(value: string): boolean {
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    return false;
  }
  if (url.username || url.password || url.hash) {
    return false;
  }
  return (
    url.protocol === "https:" ||
    (import.meta.env.DEV &&
      url.protocol === "http:" &&
      url.hostname === "127.0.0.1")
  );
}

function toLowerHex(buffer: ArrayBuffer): string {
  return Array.from(new Uint8Array(buffer), (byte) =>
    byte.toString(16).padStart(2, "0"),
  ).join("");
}

function failure(
  stage: AgentAttachmentUploadStage,
  kind: AgentAttachmentUploadFailure["kind"],
  status?: number,
): AgentAttachmentUploadFailure {
  return status === undefined ? { stage, kind } : { stage, kind, status };
}

function fail(
  stage: AgentAttachmentUploadStage,
  kind: AgentAttachmentUploadFailure["kind"],
  status?: number,
): { ok: false; error: AgentAttachmentUploadFailure } {
  return { ok: false, error: failure(stage, kind, status) };
}
