import { describe, expect, it } from "vitest";
import { createPaguaiInvoiceUploader } from "./paguai-proof-uploader";

const companyId = "11111111-1111-4111-8111-111111111111";
const fileId = "33333333-3333-4333-8333-333333333333";

describe("Paguai invoice uploader", () => {
  it("classifies the confirmed attachment as a request invoice", async () => {
    const calls: { input: string; init?: RequestInit }[] = [];
    const fetchImplementation: typeof fetch = async (input, init) => {
      calls.push({ input: String(input), init });
      switch (calls.length) {
        case 1:
        case 4:
          return Response.json({ requestToken: "csrf" });
        case 2:
          return Response.json(
            {
              fileId,
              processingState: "quarantined",
              uploadUrl: "https://storage.example/upload",
            },
            { status: 200 },
          );
        case 3:
          return new Response(null, { status: 200 });
        default:
          return Response.json(
            { fileId, processingState: "confirmed" },
            { status: 200 },
          );
      }
    };
    const cryptoImplementation = {
      randomUUID: () =>
        "22222222-2222-4222-8222-222222222222" as ReturnType<
          Crypto["randomUUID"]
        >,
      subtle: { digest: async () => new Uint8Array(32).buffer },
    } as const;
    const file = new File(["invoice"], "invoice.pdf", {
      type: "application/pdf",
    });

    const result = await createPaguaiInvoiceUploader({
      fetchImplementation,
      cryptoImplementation,
    }).upload({
      companyId,
      file,
      signal: new AbortController().signal,
    });

    expect(result).toMatchObject({ ok: true, invoice: { fileId } });
    expect(JSON.parse(String(calls[1]?.init?.body))).toMatchObject({
      classification: "paguai_request_invoice",
      companyId,
    });
  });
});
