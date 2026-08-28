import { afterEach, describe, expect, it, vi } from "vitest";
import { createAtendimentoAttachmentUploader } from "./atendimento-attachment-uploader";

afterEach(() => vi.unstubAllGlobals());

describe("Atendimento attachment uploader", () => {
  it("confirms a service-desk attachment without exposing the upload URL", async () => {
    const companyId = "11111111-1111-4111-8111-111111111111";
    const fileId = "22222222-2222-4222-8222-222222222222";
    const fetchSpy = vi
      .fn()
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ requestToken: "csrf" }), { status: 200 }),
      )
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            fileId,
            processingState: "quarantined",
            uploadUrl: "https://storage.example.test/upload",
          }),
          { status: 200 },
        ),
      )
      .mockResolvedValueOnce(new Response(null, { status: 200 }))
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ requestToken: "csrf" }), { status: 200 }),
      )
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ fileId, processingState: "confirmed" }), {
          status: 200,
        }),
      );
    const digest = vi.fn().mockResolvedValue(new Uint8Array([0xab]).buffer);
    const result = await createAtendimentoAttachmentUploader({
      cryptoImplementation: {
        randomUUID: vi
          .fn()
          .mockReturnValue("33333333-3333-4333-8333-333333333333"),
        subtle: { digest },
      },
      fetchImplementation: fetchSpy,
      now: () => new Date("2026-08-26T00:00:00.000Z"),
    }).upload({
      companyId,
      file: new File(["evidence"], "evidence.pdf", { type: "application/pdf" }),
      signal: new AbortController().signal,
    });

    expect(result).toEqual({ attachmentFileId: fileId, ok: true });
    expect(JSON.parse(String(fetchSpy.mock.calls[1]?.[1]?.body))).toMatchObject(
      {
        classification: "service_desk_attachment",
        companyId,
        origin: "web-atendimento-card",
      },
    );
    expect(fetchSpy.mock.calls[2]?.[0]).toBe(
      "https://storage.example.test/upload",
    );
  });
});
