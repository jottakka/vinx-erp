import { z } from "zod";
import {
  fetchSameOriginBff,
  readAntiforgeryTokenBff,
  type SameOriginInit,
} from "../../shared/http/bff-response";

const antiforgeryPath = "/foundation/identity/browser-session/antiforgery";
const intentionPath = "/foundation/files/upload-intentions";
const confirmationPath = "/foundation/files/confirmations";
const maxFileSizeBytes = 26_214_400;
const retentionMilliseconds = 30 * 24 * 60 * 60 * 1000;
const allowedMimeTypes = new Set([
  "application/pdf",
  "image/jpeg",
  "image/png",
]);

const intentionSchema = z
  .object({
    fileId: z.uuid(),
    processingState: z.literal("quarantined"),
    uploadUrl: z.string().min(1),
  })
  .strict();
const confirmationSchema = z
  .object({ fileId: z.uuid(), processingState: z.literal("confirmed") })
  .strict();

export type AtendimentoAttachmentUploadResult =
  | { ok: true; attachmentFileId: string }
  | { ok: false; message: string };

type UploadInput = { companyId: string; file: File; signal: AbortSignal };
type UploaderOptions = {
  fetchImplementation?: typeof fetch;
  cryptoImplementation?: Pick<Crypto, "randomUUID"> & {
    subtle: Pick<SubtleCrypto, "digest">;
  };
  now?: () => Date;
};

/** Confirma um único anexo privado do Atendimento antes de seu ID entrar no comando do cartão. */
export function createAtendimentoAttachmentUploader({
  fetchImplementation = globalThis.fetch,
  cryptoImplementation = globalThis.crypto,
  now = () => new Date(),
}: UploaderOptions = {}) {
  async function bff(path: string, init: SameOriginInit = {}) {
    return fetchSameOriginBff(fetchImplementation, path, init, () => ({
      unavailable: true,
    }));
  }

  async function token(signal: AbortSignal): Promise<string | null> {
    const result = await readAntiforgeryTokenBff(
      (init) => bff(antiforgeryPath, { ...init, signal }),
      () => ({ unavailable: true }),
      async () => ({ unavailable: true }),
      signal,
    );
    return result && "requestToken" in result ? result.requestToken : null;
  }

  async function post(path: string, body: object, signal: AbortSignal) {
    const requestToken = await token(signal);
    if (!requestToken || signal.aborted) return null;
    const result = await bff(path, {
      body: JSON.stringify(body),
      headers: {
        Accept: "application/json",
        "Content-Type": "application/json",
        "X-CSRF-TOKEN": requestToken,
      },
      mayHaveCommittedOnAbort: true,
      method: "POST",
      signal,
    });
    return result && "response" in result ? result.response : null;
  }

  return {
    async upload({
      companyId,
      file,
      signal,
    }: UploadInput): Promise<AtendimentoAttachmentUploadResult> {
      if (
        signal.aborted ||
        !file.name.trim() ||
        !allowedMimeTypes.has(file.type) ||
        file.size < 1 ||
        file.size > maxFileSizeBytes
      )
        return {
          ok: false,
          message: "Selecione um PDF, JPEG ou PNG de até 25 MiB.",
        };

      try {
        const bytes = await file.arrayBuffer();
        if (signal.aborted) return { ok: false, message: "Envio cancelado." };
        const checksum = `sha256:${Array.from(
          new Uint8Array(
            await cryptoImplementation.subtle.digest("SHA-256", bytes),
          ),
          (byte) => byte.toString(16).padStart(2, "0"),
        ).join("")}`;
        const intention = await post(
          intentionPath,
          {
            checksum,
            classification: "service_desk_attachment",
            companyId,
            fileName: file.name,
            mimeType: file.type,
            origin: "web-atendimento-card",
            requestId: cryptoImplementation.randomUUID(),
            retentionUntil: new Date(
              now().getTime() + retentionMilliseconds,
            ).toISOString(),
            sizeBytes: file.size,
          },
          signal,
        );
        if (intention?.status !== 200) throw new Error("intention");
        const intent = intentionSchema.safeParse(await intention.json());
        if (!intent.success || !isAllowedUploadUrl(intent.data.uploadUrl))
          throw new Error("intention");
        const stored = await fetchImplementation(intent.data.uploadUrl, {
          body: bytes,
          credentials: "omit",
          headers: { "Content-Type": file.type },
          method: "PUT",
          redirect: "error",
          signal,
        });
        if (!stored.ok || signal.aborted) throw new Error("storage");
        const confirmation = await post(
          confirmationPath,
          {
            companyId,
            fileId: intent.data.fileId,
            requestId: cryptoImplementation.randomUUID(),
          },
          signal,
        );
        if (confirmation?.status !== 200) throw new Error("confirmation");
        const confirmed = confirmationSchema.safeParse(
          await confirmation.json(),
        );
        if (!confirmed.success || confirmed.data.fileId !== intent.data.fileId)
          throw new Error("confirmation");
        return { ok: true, attachmentFileId: intent.data.fileId };
      } catch {
        return signal.aborted
          ? { ok: false, message: "Envio cancelado." }
          : {
              ok: false,
              message: "Não foi possível confirmar o anexo. Tente novamente.",
            };
      }
    },
  };
}

function isAllowedUploadUrl(value: string): boolean {
  try {
    const url = new URL(value);
    return (
      !url.username &&
      !url.password &&
      !url.hash &&
      (url.protocol === "https:" ||
        (import.meta.env.DEV &&
          url.protocol === "http:" &&
          url.hostname === "127.0.0.1"))
    );
  } catch {
    return false;
  }
}
