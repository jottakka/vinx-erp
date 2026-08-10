import { describe, expect, it } from "vitest";

const excludedDirectory = /^(?:app\/theme|test)(?:\/|$)/;
const excludedFile = /(?:\.test|\.spec)\.[cm]?[jt]sx?$/;

function normalizeSourcePath(filePath: string) {
  return filePath.replace(/^(?:\.\.\/)+/, "");
}

function shouldScanSourceFile(filePath: string) {
  const relativePath = normalizeSourcePath(filePath);

  return (
    !filePath.startsWith("./") &&
    !excludedDirectory.test(relativePath) &&
    !excludedFile.test(relativePath)
  );
}

function violations(source: string) {
  const findings: string[] = [];

  if (/(?:^|[^\w])#[\da-f]{3,8}\b/i.test(source)) {
    findings.push("hex color");
  }

  if (/\b(?:rgb|rgba|hsl|hsla|hwb|oklch|lab|lch|color)\s*\(/i.test(source)) {
    findings.push("color function");
  }

  if (/\b(?:linear|radial|conic)-gradient\s*\(/i.test(source)) {
    findings.push("gradient");
  }

  const svgAttribute = /\b(?:fill|stroke)\s*=\s*(["'])(.*?)\1/gi;
  for (const match of source.matchAll(svgAttribute)) {
    const value = match[2].trim();
    if (
      value !== "currentColor" &&
      value !== "none" &&
      !value.startsWith("var(--")
    ) {
      findings.push("literal SVG fill/stroke");
      break;
    }
  }

  if (
    /(?:from\s+["'][^"']*palette-primitives|import\s*\(\s*["'][^"']*palette-primitives)/i.test(
      source,
    )
  ) {
    findings.push("private palette primitive import");
  }

  return findings;
}

describe("semantic color boundary", () => {
  it("excludes test paths but scans production paths after glob normalization", () => {
    const sourceFiles = {
      "../../features/orders.test.ts": "const color = '#fff';",
      "../../../test/fixture.ts": "const color = '#fff';",
      "../../features/orders.ts": "const color = '#fff';",
    };
    const findings = Object.entries(sourceFiles).flatMap(
      ([filePath, source]) =>
        shouldScanSourceFile(filePath)
          ? violations(source).map((finding) => `${filePath}: ${finding}`)
          : [],
    );

    expect(findings).toEqual(["../../features/orders.ts: hex color"]);
  });

  it("keeps literal colors private to the theme primitives", () => {
    const sourceFiles = import.meta.glob("../../**/*.{ts,tsx,js,jsx}", {
      eager: true,
      import: "default",
      query: "?raw",
    }) as Record<string, string>;
    const findings = Object.entries(sourceFiles).flatMap(
      ([filePath, source]) => {
        if (!shouldScanSourceFile(filePath)) {
          return [];
        }

        return violations(source).map(
          (finding) => `${normalizeSourcePath(filePath)}: ${finding}`,
        );
      },
    );

    expect(findings).toEqual([]);
  });
});
