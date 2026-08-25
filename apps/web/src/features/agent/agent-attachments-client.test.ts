import { describe, expect, it, vi } from "vitest";
import { createAgentAttachmentClient } from "./agent-attachments-client";

const companyId = "33333333-3333-4333-8333-333333333333";
const fileId = "44444444-4444-4444-8444-444444444444";
const now = new Date("2026-08-25T12:00:00.000Z");
const expectedChecksum =
  "sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

function fileWithBytes(
  name = "evidence.pdf",
  type = "application/pdf",
  bytes = new Uint8Array([0x61, 0x62, 0x63]),
): File {
  const file = new File([bytes], name, { type });
  Object.defineProperty(file, "arrayBuffer", {
    value: async () => bytes.buffer.slice(0),
  });
  return file;
}

function antiforgeryResponse() {
  return new Response(JSON.stringify({ requestToken: "csrf-token" }), {
    headers: { "Content-Type": "application/json" },
    status: 200,
  });
}

function intentionResponse() {
  return new Response(
    JSON.stringify({
      fileId,
      processingState: "quarantined",
      uploadUrl: "https://storage.test.invalid/upload-object",
    }),
    { headers: { "Content-Type": "application/json" }, status: 200 },
  );
}

function confirmationResponse(id = fileId, state = "confirmed") {
  return new Response(JSON.stringify({ fileId: id, processingState: state }), {
    headers: { "Content-Type": "application/json" },
    status: 200,
  });
}

describe("agent attachment upload client", () => {
  it("rejects a disallowed type and an empty file before network access", async () => {
    const fetchImplementation = vi.fn<typeof fetch>();
    const client = createAgentAttachmentClient({
      fetchImplementation,
      now: () => now,
    });

    const wrongType = await client.upload({
      companyId,
      file: fileWithBytes("evidence.gif", "image/gif"),
      signal: new AbortController().signal,
    });
    const empty = await client.upload({
      companyId,
      file: fileWithBytes("evidence.pdf", "application/pdf", new Uint8Array()),
      signal: new AbortController().signal,
    });

    expect(wrongType).toEqual({
      ok: false,
      error: { kind: "validation", stage: "validation" },
    });
    expect(empty).toEqual({
      ok: false,
      error: { kind: "validation", stage: "validation" },
    });
    expect(fetchImplementation).not.toHaveBeenCalled();
  });

  it("uploads checked bytes through the exact BFF and storage contract without retaining the upload URL", async () => {
    const fetchImplementation = vi
      .fn<typeof fetch>()
      .mockResolvedValueOnce(antiforgeryResponse())
      .mockResolvedValueOnce(intentionResponse())
      .mockResolvedValueOnce(new Response(null, { status: 204 }))
      .mockResolvedValueOnce(antiforgeryResponse())
      .mockResolvedValueOnce(confirmationResponse());
    const client = createAgentAttachmentClient({
      fetchImplementation,
      now: () => now,
    });

    const result = await client.upload({
      companyId,
      file: fileWithBytes(),
      signal: new AbortController().signal,
    });

    expect(result).toEqual({
      ok: true,
      attachment: {
        fileId,
        fileName: "evidence.pdf",
        mimeType: "application/pdf",
        sizeBytes: 3,
      },
    });
    expect(JSON.stringify(result)).not.toContain("storage.test.invalid");
    expect(fetchImplementation).toHaveBeenCalledTimes(5);

    const [
      antiforgeryForIntention,
      intention,
      storage,
      antiforgeryForConfirm,
      confirmation,
    ] = fetchImplementation.mock.calls;
    expect(antiforgeryForIntention).toEqual([
      "/foundation/identity/browser-session/antiforgery",
      expect.objectContaining({
        cache: "no-store",
        credentials: "same-origin",
        method: "GET",
        mode: "same-origin",
        redirect: "error",
      }),
    ]);
    expect(JSON.parse(intention[1]?.body as string)).toEqual({
      checksum: expectedChecksum,
      classification: "agentic_attachment",
      companyId,
      fileName: "evidence.pdf",
      mimeType: "application/pdf",
      origin: "web-agent-chat",
      requestId: expect.stringMatching(
        /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/,
      ),
      retentionUntil: "2026-09-24T12:00:00.000Z",
      sizeBytes: 3,
    });
    expect(intention).toEqual([
      "/foundation/files/upload-intentions",
      expect.objectContaining({
        cache: "no-store",
        credentials: "same-origin",
        headers: {
          Accept: "application/json",
          "Content-Type": "application/json",
          "X-CSRF-TOKEN": "csrf-token",
        },
        method: "POST",
        mode: "same-origin",
        redirect: "error",
      }),
    ]);
    expect(storage).toEqual([
      "https://storage.test.invalid/upload-object",
      expect.objectContaining({
        body: expect.any(ArrayBuffer),
        credentials: "omit",
        headers: { "Content-Type": "application/pdf" },
        method: "PUT",
        redirect: "error",
      }),
    ]);
    expect(antiforgeryForConfirm[0]).toBe(
      "/foundation/identity/browser-session/antiforgery",
    );
    expect(JSON.parse(confirmation[1]?.body as string)).toEqual({
      companyId,
      fileId,
      requestId: expect.stringMatching(
        /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/,
      ),
    });
    expect(confirmation[1]).toEqual(
      expect.objectContaining({
        cache: "no-store",
        credentials: "same-origin",
        headers: {
          Accept: "application/json",
          "Content-Type": "application/json",
          "X-CSRF-TOKEN": "csrf-token",
        },
        method: "POST",
        mode: "same-origin",
        redirect: "error",
      }),
    );
  });

  it("fails closed at confirmation when the server confirms another file", async () => {
    const fetchImplementation = vi
      .fn<typeof fetch>()
      .mockResolvedValueOnce(antiforgeryResponse())
      .mockResolvedValueOnce(intentionResponse())
      .mockResolvedValueOnce(new Response(null, { status: 200 }))
      .mockResolvedValueOnce(antiforgeryResponse())
      .mockResolvedValueOnce(
        confirmationResponse("55555555-5555-4555-8555-555555555555"),
      );
    const client = createAgentAttachmentClient({
      fetchImplementation,
      now: () => now,
    });

    const result = await client.upload({
      companyId,
      file: fileWithBytes(),
      signal: new AbortController().signal,
    });

    expect(result).toEqual({
      ok: false,
      error: { kind: "protocol", stage: "confirmation", status: 200 },
    });
    expect(JSON.stringify(result)).not.toContain("storage.test.invalid");
  });

  it("maps a storage network failure to a fixed typed error with no retry", async () => {
    const fetchImplementation = vi
      .fn<typeof fetch>()
      .mockResolvedValueOnce(antiforgeryResponse())
      .mockResolvedValueOnce(intentionResponse())
      .mockRejectedValueOnce(new Error());
    const client = createAgentAttachmentClient({
      fetchImplementation,
      now: () => now,
    });

    const result = await client.upload({
      companyId,
      file: fileWithBytes(),
      signal: new AbortController().signal,
    });

    expect(result).toEqual({
      ok: false,
      error: { kind: "network", stage: "storage" },
    });
    expect(fetchImplementation).toHaveBeenCalledTimes(3);
  });

  it("rejects an insecure intention target before making the storage request", async () => {
    const fetchImplementation = vi
      .fn<typeof fetch>()
      .mockResolvedValueOnce(antiforgeryResponse())
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            fileId,
            processingState: "quarantined",
            uploadUrl: "http://storage.test.invalid/upload-object",
          }),
          { headers: { "Content-Type": "application/json" }, status: 200 },
        ),
      );
    const client = createAgentAttachmentClient({
      fetchImplementation,
      now: () => now,
    });

    const result = await client.upload({
      companyId,
      file: fileWithBytes(),
      signal: new AbortController().signal,
    });

    expect(result).toEqual({
      ok: false,
      error: { kind: "protocol", stage: "intention", status: 200 },
    });
    expect(fetchImplementation).toHaveBeenCalledTimes(2);
  });

  it("does not read a non-successful storage response body", async () => {
    const rejectedStorageResponse = new Response("untrusted response body", {
      status: 403,
    });
    const fetchImplementation = vi
      .fn<typeof fetch>()
      .mockResolvedValueOnce(antiforgeryResponse())
      .mockResolvedValueOnce(intentionResponse())
      .mockResolvedValueOnce(rejectedStorageResponse);
    const client = createAgentAttachmentClient({
      fetchImplementation,
      now: () => now,
    });

    const result = await client.upload({
      companyId,
      file: fileWithBytes(),
      signal: new AbortController().signal,
    });

    expect(result).toEqual({
      ok: false,
      error: { kind: "protocol", stage: "storage", status: 403 },
    });
    expect(rejectedStorageResponse.bodyUsed).toBe(false);
  });

  it("fails closed when confirmation does not return the confirmed state", async () => {
    const fetchImplementation = vi
      .fn<typeof fetch>()
      .mockResolvedValueOnce(antiforgeryResponse())
      .mockResolvedValueOnce(intentionResponse())
      .mockResolvedValueOnce(new Response(null, { status: 200 }))
      .mockResolvedValueOnce(antiforgeryResponse())
      .mockResolvedValueOnce(confirmationResponse(fileId, "quarantined"));
    const client = createAgentAttachmentClient({
      fetchImplementation,
      now: () => now,
    });

    const result = await client.upload({
      companyId,
      file: fileWithBytes(),
      signal: new AbortController().signal,
    });

    expect(result).toEqual({
      ok: false,
      error: { kind: "protocol", stage: "confirmation", status: 200 },
    });
  });

  it("reports an aborted storage operation without starting confirmation", async () => {
    const abortError = Object.assign(new Error(), { name: "AbortError" });
    const controller = new AbortController();
    const fetchImplementation = vi
      .fn<typeof fetch>()
      .mockResolvedValueOnce(antiforgeryResponse())
      .mockResolvedValueOnce(intentionResponse())
      .mockImplementationOnce(() => {
        controller.abort();
        return Promise.reject(abortError);
      });
    const client = createAgentAttachmentClient({
      fetchImplementation,
      now: () => now,
    });

    const result = await client.upload({
      companyId,
      file: fileWithBytes(),
      signal: controller.signal,
    });

    expect(result).toEqual({
      ok: false,
      error: { kind: "aborted", stage: "storage" },
    });
    expect(fetchImplementation).toHaveBeenCalledTimes(3);
  });

  it("stops after a digest that was cancelled before any BFF request", async () => {
    const controller = new AbortController();
    const fetchImplementation = vi.fn<typeof fetch>();
    const client = createAgentAttachmentClient({
      cryptoImplementation: {
        randomUUID: () => "11111111-1111-4111-8111-111111111111",
        subtle: {
          digest: async () => {
            controller.abort();
            return new ArrayBuffer(32);
          },
        } as Pick<SubtleCrypto, "digest">,
      },
      fetchImplementation,
      now: () => now,
    });

    const result = await client.upload({
      companyId,
      file: fileWithBytes(),
      signal: controller.signal,
    });

    expect(result).toEqual({
      ok: false,
      error: { kind: "aborted", stage: "validation" },
    });
    expect(fetchImplementation).not.toHaveBeenCalled();
  });

  it("maps a confirmation request abort to the confirmation stage", async () => {
    const abortError = Object.assign(new Error(), { name: "AbortError" });
    const controller = new AbortController();
    const fetchImplementation = vi
      .fn<typeof fetch>()
      .mockResolvedValueOnce(antiforgeryResponse())
      .mockResolvedValueOnce(intentionResponse())
      .mockResolvedValueOnce(new Response(null, { status: 200 }))
      .mockImplementationOnce(() => {
        controller.abort();
        return Promise.reject(abortError);
      });
    const client = createAgentAttachmentClient({
      fetchImplementation,
      now: () => now,
    });

    const result = await client.upload({
      companyId,
      file: fileWithBytes(),
      signal: controller.signal,
    });

    expect(result).toEqual({
      ok: false,
      error: { kind: "aborted", stage: "confirmation" },
    });
    expect(fetchImplementation).toHaveBeenCalledTimes(4);
  });

  it("fails closed when cancellation happens while confirmation is parsed", async () => {
    const controller = new AbortController();
    const confirmation = confirmationResponse();
    vi.spyOn(confirmation, "json").mockImplementation(async () => {
      controller.abort();
      return { fileId, processingState: "confirmed" };
    });
    const fetchImplementation = vi
      .fn<typeof fetch>()
      .mockResolvedValueOnce(antiforgeryResponse())
      .mockResolvedValueOnce(intentionResponse())
      .mockResolvedValueOnce(new Response(null, { status: 200 }))
      .mockResolvedValueOnce(antiforgeryResponse())
      .mockResolvedValueOnce(confirmation);
    const client = createAgentAttachmentClient({
      fetchImplementation,
      now: () => now,
    });

    const result = await client.upload({
      companyId,
      file: fileWithBytes(),
      signal: controller.signal,
    });

    expect(result).toEqual({
      ok: false,
      error: { kind: "aborted", stage: "confirmation" },
    });
  });
});
