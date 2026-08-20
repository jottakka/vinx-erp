import { describe, expect, it } from "vitest";
import { z } from "zod";
import {
  isCanonicalUuid,
  readCorrelationId,
  readErrorCode,
} from "./bff-response";

const correlationId = "01234567-89ab-cdef-0123-456789abcdef";
const emptyUuid = "00000000-0000-0000-0000-000000000000";

function responseWithHeaders(headers: Record<string, string>) {
  return new Response(null, { headers });
}

describe("BFF response helpers", () => {
  it("accepts canonical UUIDs and rejects malformed or empty values by default", () => {
    expect(isCanonicalUuid(correlationId)).toBe(true);
    expect(isCanonicalUuid("not-a-uuid")).toBe(false);
    expect(isCanonicalUuid(emptyUuid)).toBe(false);
    expect(
      readCorrelationId(
        responseWithHeaders({ "X-Correlation-ID": correlationId }),
      ),
    ).toBe(correlationId);
    expect(
      readCorrelationId(
        responseWithHeaders({ "X-Correlation-ID": "not-a-uuid" }),
      ),
    ).toBeUndefined();
    expect(
      readCorrelationId(responseWithHeaders({ "X-Correlation-ID": emptyUuid })),
    ).toBeUndefined();
  });

  it("can preserve clients that accept an empty UUID correlation id", () => {
    expect(isCanonicalUuid(emptyUuid, { allowEmpty: true })).toBe(true);
    expect(
      readCorrelationId(
        responseWithHeaders({ "X-Correlation-ID": emptyUuid }),
        { allowEmpty: true },
      ),
    ).toBe(emptyUuid);
  });

  it("reads only allow-listed codes from JSON responses", async () => {
    const schema = z.enum(["conflict", "unavailable"]);

    await expect(
      readErrorCode(
        new Response(JSON.stringify({ code: "conflict" }), {
          headers: { "Content-Type": "application/json" },
        }),
        schema,
      ),
    ).resolves.toBe("conflict");
    await expect(
      readErrorCode(
        new Response(JSON.stringify({ code: "unexpected" }), {
          headers: { "Content-Type": "application/json" },
        }),
        schema,
      ),
    ).resolves.toBeUndefined();
    await expect(
      readErrorCode(
        new Response("conflict", { headers: { "Content-Type": "text/plain" } }),
        schema,
      ),
    ).resolves.toBeUndefined();
  });
});
