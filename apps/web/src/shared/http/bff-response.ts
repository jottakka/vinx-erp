import type { z } from "zod";

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
