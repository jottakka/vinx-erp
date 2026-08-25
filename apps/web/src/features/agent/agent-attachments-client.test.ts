import { describe, expect, it, vi } from "vitest";
import {
  type AgentAttachmentUploadResult,
  createAgentAttachmentClient,
} from "./agent-attachments-client";

const companyId = "33333333-3333-4333-8333-333333333333";
const fileId = "44444444-4444-4444-8444-444444444444";
const otherFileId = "55555555-5555-4555-8555-555555555555";
const now = new Date("2026-08-25T12:00:00.000Z");
const maxFileSizeBytes = 26_214_400;
const uploadTarget = "https://storage.test.invalid/upload-object";
const expectedChecksum =
  "sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

type FetchStep = Error | Response | (() => Promise<Response>);

function fileWith({
  bytes = new Uint8Array([0x61, 0x62, 0x63]),
  name = "evidence.pdf",
  onRead,
  size = bytes.byteLength,
  type = "application/pdf",
}: {
  bytes?: Uint8Array<ArrayBuffer>;
  name?: string;
  onRead?: () => void;
  size?: number;
  type?: string;
} = {}): File {
  const file = new File([bytes], name, { type });
  Object.defineProperties(file, {
    arrayBuffer: {
      value: async () => {
        onRead?.();
        return bytes.buffer.slice(0);
      },
    },
    size: { value: size },
  });
  return file;
}

function response(body: unknown, status = 200, contentType = true) {
  return new Response(typeof body === "string" ? body : JSON.stringify(body), {
    headers: contentType ? { "Content-Type": "application/json" } : {},
    status,
  });
}

function antiforgery() {
  return response({ requestToken: "csrf-token" });
}

function intention(target = uploadTarget) {
  return response({
    fileId,
    processingState: "quarantined",
    uploadUrl: target,
  });
}

function confirmation(id = fileId, state = "confirmed") {
  return response({ fileId: id, processingState: state });
}

function successSteps(last: FetchStep = confirmation()): FetchStep[] {
  return [
    antiforgery(),
    intention(),
    new Response(null, { status: 204 }),
    antiforgery(),
    last,
  ];
}

function queuedFetch(steps: FetchStep[]) {
  return vi.fn<typeof fetch>(async () => {
    const step = steps.shift();
    if (step instanceof Error) throw step;
    if (typeof step === "function") return step();
    if (!step) throw new Error();
    return step;
  });
}

async function upload(
  steps: FetchStep[],
  {
    controller = new AbortController(),
    file = fileWith(),
  }: { controller?: AbortController; file?: File } = {},
) {
  const fetchImplementation = queuedFetch(steps);
  const result = await createAgentAttachmentClient({
    fetchImplementation,
    now: () => now,
  }).upload({ companyId, file, signal: controller.signal });
  return { fetchImplementation, result };
}

function expectFailure(
  result: AgentAttachmentUploadResult,
  error: Record<string, unknown>,
) {
  expect(result).toEqual({ ok: false, error });
  expect(JSON.stringify(result)).not.toContain("storage.test.invalid");
  expect(JSON.stringify(result)).not.toContain("untrusted response body");
  expect(JSON.stringify(result)).not.toContain("raw failure");
}

describe("agent attachment upload client", () => {
  it.each([
    ["blank name", { name: "   " }],
    ["empty file", { size: 0 }],
    ["file over 25 MiB", { size: maxFileSizeBytes + 1 }],
    ["WebP", { type: "image/webp" }],
  ])("rejects %s before network access", async (_case, file) => {
    const { fetchImplementation, result } = await upload([], {
      file: fileWith(file),
    });

    expectFailure(result, { kind: "validation", stage: "validation" });
    expect(fetchImplementation).not.toHaveBeenCalled();
  });

  it.each(["application/pdf", "image/jpeg", "image/png"])(
    "accepts %s",
    async (type) => {
      const { result } = await upload(successSteps(), {
        file: fileWith({ type }),
      });

      expect(result).toMatchObject({
        ok: true,
        attachment: { mimeType: type },
      });
    },
  );

  it("aborts after digest before making a BFF request", async () => {
    const controller = new AbortController();
    const fetchImplementation = queuedFetch([]);
    const result = await createAgentAttachmentClient({
      cryptoImplementation: {
        randomUUID: () => fileId,
        subtle: {
          digest: async () => {
            controller.abort();
            return new ArrayBuffer(32);
          },
        },
      },
      fetchImplementation,
      now: () => now,
    }).upload({ companyId, file: fileWith(), signal: controller.signal });

    expectFailure(result, { kind: "aborted", stage: "validation" });
    expect(fetchImplementation).not.toHaveBeenCalled();
  });

  it("accepts exactly 25 MiB and reads the original bytes once for storage", async () => {
    let reads = 0;
    const bytes = new Uint8Array([0x61, 0x62, 0x63]);
    const { fetchImplementation, result } = await upload(successSteps(), {
      file: fileWith({ bytes, onRead: () => reads++, size: maxFileSizeBytes }),
    });

    expect(result).toMatchObject({
      ok: true,
      attachment: { sizeBytes: maxFileSizeBytes },
    });
    expect(reads).toBe(1);
    expect(
      new Uint8Array(fetchImplementation.mock.calls[2][1]?.body as ArrayBuffer),
    ).toEqual(bytes);
  });

  it("sends exact BFF payloads with distinct request IDs and no retained upload target", async () => {
    const { fetchImplementation, result } = await upload(successSteps());

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
      ,
      intentionCall,
      storageCall,
      antiforgeryForConfirm,
      confirmationCall,
    ] = fetchImplementation.mock.calls;
    const intentionBody = JSON.parse(intentionCall[1]?.body as string);
    const confirmationBody = JSON.parse(confirmationCall[1]?.body as string);
    expect(intentionBody).toEqual({
      checksum: expectedChecksum,
      classification: "agentic_attachment",
      companyId,
      fileName: "evidence.pdf",
      mimeType: "application/pdf",
      origin: "web-agent-chat",
      requestId: expect.stringMatching(/^[0-9a-f-]{36}$/),
      retentionUntil: "2026-09-24T12:00:00.000Z",
      sizeBytes: 3,
    });
    expect(confirmationBody).toEqual({
      companyId,
      fileId,
      requestId: expect.stringMatching(/^[0-9a-f-]{36}$/),
    });
    expect(intentionBody.requestId).not.toBe(confirmationBody.requestId);
    expect(intentionCall[1]).toEqual(
      expect.objectContaining({
        cache: "no-store",
        credentials: "same-origin",
        headers: expect.objectContaining({ "X-CSRF-TOKEN": "csrf-token" }),
        method: "POST",
        mode: "same-origin",
        redirect: "error",
      }),
    );
    expect(antiforgeryForConfirm[0]).toBe(
      "/foundation/identity/browser-session/antiforgery",
    );
    expect(storageCall[1]).toEqual(
      expect.objectContaining({
        credentials: "omit",
        headers: { "Content-Type": "application/pdf" },
        method: "PUT",
        redirect: "error",
      }),
    );
  });

  it.each([
    ["HTTPS", uploadTarget, true],
    ["DEV loopback HTTP", "http://127.0.0.1:54321/object", true],
    ["non-loopback HTTP", "http://storage.test.invalid/object", false],
    ["userinfo", "https://user@storage.test.invalid/object", false],
    ["fragment", "https://storage.test.invalid/object#fragment", false],
    ["invalid target", "not an absolute URL", false],
  ])(
    "accepts only the allowed %s upload target",
    async (_case, target, accepted) => {
      const { fetchImplementation, result } = await upload([
        antiforgery(),
        intention(target),
        ...successSteps().slice(2),
      ]);

      if (accepted) {
        expect(result).toMatchObject({ ok: true });
      } else {
        expectFailure(result, {
          kind: "protocol",
          stage: "intention",
          status: 200,
        });
        expect(fetchImplementation).toHaveBeenCalledTimes(2);
      }
    },
  );

  it.each([
    [
      "HTTP",
      [antiforgery(), response({}, 500)],
      { kind: "protocol", stage: "intention", status: 500 },
    ],
    [
      "invalid body",
      [antiforgery(), response("untrusted response body")],
      { kind: "protocol", stage: "intention", status: 200 },
    ],
    [
      "network",
      [antiforgery(), new Error("raw failure")],
      { kind: "network", stage: "intention" },
    ],
  ])(
    "maps intention %s failures without leaking details",
    async (_case, steps, error) => {
      const { result } = await upload(steps);

      expectFailure(result, error);
    },
  );

  it("stops at intention when JSON parsing observes cancellation", async () => {
    const controller = new AbortController();
    const abortedIntention = intention();
    vi.spyOn(abortedIntention, "json").mockImplementation(async () => {
      controller.abort();
      return {
        fileId,
        processingState: "quarantined",
        uploadUrl: uploadTarget,
      };
    });
    const { fetchImplementation, result } = await upload(
      [antiforgery(), abortedIntention],
      { controller },
    );

    expectFailure(result, { kind: "aborted", stage: "intention" });
    expect(fetchImplementation).toHaveBeenCalledTimes(2);
  });

  it("stops at intention when antiforgery parsing observes cancellation", async () => {
    const controller = new AbortController();
    const cancelledAntiforgery = antiforgery();
    vi.spyOn(cancelledAntiforgery, "json").mockImplementation(async () => {
      controller.abort();
      return { requestToken: "csrf-token" };
    });
    const { fetchImplementation, result } = await upload(
      [cancelledAntiforgery],
      {
        controller,
      },
    );

    expectFailure(result, { kind: "aborted", stage: "intention" });
    expect(fetchImplementation).toHaveBeenCalledTimes(1);
  });

  it.each([
    [
      "HTTP",
      response({}, 500),
      { kind: "protocol", stage: "confirmation", status: 500 },
    ],
    [
      "invalid body",
      response("untrusted response body"),
      { kind: "protocol", stage: "confirmation", status: 200 },
    ],
    [
      "mismatched file",
      confirmation(otherFileId),
      { kind: "protocol", stage: "confirmation", status: 200 },
    ],
    [
      "wrong state",
      confirmation(fileId, "quarantined"),
      { kind: "protocol", stage: "confirmation", status: 200 },
    ],
    [
      "network",
      new Error("raw failure"),
      { kind: "network", stage: "confirmation" },
    ],
  ])(
    "maps confirmation %s failures without leaking details",
    async (_case, last, error) => {
      const { result } = await upload(successSteps(last));

      expectFailure(result, error);
    },
  );

  it("does not consume a failed storage response body", async () => {
    const rejectedStorage = response("untrusted response body", 403, false);
    const { result } = await upload([
      antiforgery(),
      intention(),
      rejectedStorage,
    ]);

    expectFailure(result, { kind: "protocol", stage: "storage", status: 403 });
    expect(rejectedStorage.bodyUsed).toBe(false);
  });

  it("stops at confirmation when JSON parsing observes cancellation", async () => {
    const controller = new AbortController();
    const abortedConfirmation = confirmation();
    vi.spyOn(abortedConfirmation, "json").mockImplementation(async () => {
      controller.abort();
      return { fileId, processingState: "confirmed" };
    });
    const { result } = await upload(successSteps(abortedConfirmation), {
      controller,
    });

    expectFailure(result, { kind: "aborted", stage: "confirmation" });
  });

  it.each([
    [
      "HTTP",
      response("untrusted response body", 403, false),
      { kind: "protocol", stage: "storage", status: 403 },
    ],
    [
      "network",
      new Error("raw failure"),
      { kind: "network", stage: "storage" },
    ],
  ])(
    "maps storage %s failures without reading response details",
    async (_case, storage, error) => {
      const { result } = await upload([antiforgery(), intention(), storage]);

      expectFailure(result, error);
    },
  );

  it("does not advance when storage resolves after cancellation", async () => {
    const controller = new AbortController();
    let resolveStorage: (value: Response) => void = () => {};
    const storage = new Promise<Response>(
      (resolve) => (resolveStorage = resolve),
    );
    const fetchImplementation = queuedFetch([
      antiforgery(),
      intention(),
      () => storage,
      antiforgery(),
      confirmation(),
    ]);
    const pending = createAgentAttachmentClient({
      fetchImplementation,
      now: () => now,
    }).upload({ companyId, file: fileWith(), signal: controller.signal });
    await vi.waitFor(() =>
      expect(fetchImplementation).toHaveBeenCalledTimes(3),
    );
    controller.abort();
    resolveStorage(new Response(null, { status: 204 }));

    expectFailure(await pending, { kind: "aborted", stage: "storage" });
    expect(fetchImplementation).toHaveBeenCalledTimes(3);
  });
});
