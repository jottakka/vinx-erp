import { describe, expect, it } from "vitest";
import { z } from "zod";
import {
  bffFailure,
  fetchSameOriginBff,
  isAbortError,
  isCanonicalUuid,
  readAntiforgeryTokenBff,
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

describe("isAbortError", () => {
  it("detects aborted signals and AbortError, nothing else", () => {
    const controller = new AbortController();
    controller.abort();
    expect(isAbortError(new Error("boom"), controller.signal)).toBe(true);
    const abortError = new Error("The operation was aborted.");
    abortError.name = "AbortError";
    expect(isAbortError(abortError, undefined)).toBe(true);
    expect(isAbortError(new TypeError("fetch failed"), undefined)).toBe(false);
    const silent = new AbortController();
    expect(isAbortError(new Error("boom"), silent.signal)).toBe(false);
  });
});

describe("bffFailure", () => {
  it("keeps only defined optional fields", () => {
    expect(
      bffFailure<{ kind: "network"; status?: number; code?: string }>(
        "network",
        { status: 503, code: undefined },
      ),
    ).toEqual({ ok: false, error: { kind: "network", status: 503 } });
    expect(bffFailure<{ kind: "aborted" }>("aborted")).toEqual({
      ok: false,
      error: { kind: "aborted" },
    });
  });
});

describe("fetchSameOriginBff", () => {
  type TransportFailure = {
    failure: {
      kind: string;
      wasAborted: boolean;
      mayHaveCommittedOnAbort: boolean;
    };
  };

  function recorder(failures: TransportFailure["failure"][]) {
    return (
      kind: "aborted" | "network",
      details: { wasAborted: boolean; mayHaveCommittedOnAbort: boolean },
    ): TransportFailure => {
      failures.push({ kind, ...details });
      return { failure: { kind, ...details } };
    };
  }

  it("sends the same-origin BFF init and strips client-only flags", async () => {
    let captured: RequestInit | undefined;
    const fetchImplementation = async (
      _input: RequestInfo | URL,
      init?: RequestInit,
    ) => {
      captured = init;
      return new Response("{}");
    };

    const result = await fetchSameOriginBff(
      fetchImplementation,
      "/foundation/identity/browser-session/antiforgery",
      { method: "GET", mayHaveCommittedOnAbort: true },
      recorder([]),
    );

    expect("response" in result).toBe(true);
    expect(captured).toMatchObject({
      cache: "no-store",
      credentials: "same-origin",
      mode: "same-origin",
      redirect: "error",
    });
    expect(captured).not.toHaveProperty("mayHaveCommittedOnAbort");
  });

  it("fails as aborted without fetching when the signal is already aborted", async () => {
    const controller = new AbortController();
    controller.abort();
    const failures: TransportFailure["failure"][] = [];
    let fetched = false;
    const fetchImplementation = async () => {
      fetched = true;
      return new Response("{}");
    };

    const result = await fetchSameOriginBff(
      fetchImplementation,
      "/path",
      { signal: controller.signal, mayHaveCommittedOnAbort: true },
      recorder(failures),
    );

    expect(fetched).toBe(false);
    expect(result).toEqual({
      failure: {
        kind: "aborted",
        wasAborted: true,
        mayHaveCommittedOnAbort: false,
      },
    });
  });

  it("maps network errors and aborts to the typed failure with the commit flag", async () => {
    const networkFailures: TransportFailure["failure"][] = [];
    const throwingFetch = async () => {
      throw new TypeError("fetch failed");
    };
    const networkResult = await fetchSameOriginBff(
      throwingFetch,
      "/path",
      { mayHaveCommittedOnAbort: true },
      recorder(networkFailures),
    );
    expect(networkResult).toEqual({
      failure: {
        kind: "network",
        wasAborted: false,
        mayHaveCommittedOnAbort: true,
      },
    });

    const abortError = new Error("The operation was aborted.");
    abortError.name = "AbortError";
    const abortFailures: TransportFailure["failure"][] = [];
    const abortingFetch = async () => {
      throw abortError;
    };
    const abortResult = await fetchSameOriginBff(
      abortingFetch,
      "/path",
      { mayHaveCommittedOnAbort: false },
      recorder(abortFailures),
    );
    expect(abortResult).toEqual({
      failure: {
        kind: "aborted",
        wasAborted: true,
        mayHaveCommittedOnAbort: false,
      },
    });
  });
});

describe("readAntiforgeryTokenBff", () => {
  type Marker = { marker: string };

  it("reads the request token from a strict body and forwards the BFF init", async () => {
    let captured: RequestInit | undefined;
    const token = await readAntiforgeryTokenBff<Marker>(
      async (init) => {
        captured = init;
        return {
          response: new Response(JSON.stringify({ requestToken: "token-1" })),
        };
      },
      () => ({ marker: "fail" }),
      async () => ({ marker: "normalize" }),
    );

    expect(token).toEqual({ ok: true, requestToken: "token-1" });
    expect(captured).toMatchObject({
      method: "GET",
      headers: { Accept: "application/json" },
    });
  });

  it("rejects non-strict bodies as protocol failures with status", async () => {
    const statuses: Array<string | number | undefined> = [];
    const result = await readAntiforgeryTokenBff<Marker>(
      async () => ({
        response: new Response(
          JSON.stringify({ requestToken: "t", extra: true }),
          { status: 200 },
        ),
      }),
      (kind, status) => {
        statuses.push([kind, status].join(":"));
        return { marker: "fail" };
      },
      async () => ({ marker: "normalize" }),
    );

    expect(result).toEqual({ marker: "fail" });
    expect(statuses).toEqual(["protocol:200"]);
  });

  it("delegates non-ok responses to the client normalizer", async () => {
    let seenStatus = 0;
    const result = await readAntiforgeryTokenBff<Marker>(
      async () => ({ response: new Response("upstream", { status: 503 }) }),
      () => ({ marker: "fail" }),
      async (response) => {
        seenStatus = response.status;
        return { marker: "normalize" };
      },
    );

    expect(result).toEqual({ marker: "normalize" });
    expect(seenStatus).toBe(503);
  });

  it("maps a body-read rejection on an aborted signal to aborted", async () => {
    const controller = new AbortController();
    controller.abort();
    const statuses: Array<string | number | undefined> = [];
    const result = await readAntiforgeryTokenBff<Marker>(
      async () => ({
        response: new Response("{invalid json", {
          status: 200,
          headers: { "Content-Type": "application/json" },
        }),
      }),
      (kind, status) => {
        statuses.push([kind, status].join(":"));
        return { marker: "fail" };
      },
      async () => ({ marker: "normalize" }),
      controller.signal,
    );

    expect(result).toEqual({ marker: "fail" });
    expect(statuses).toEqual(["aborted:200"]);
  });
});
