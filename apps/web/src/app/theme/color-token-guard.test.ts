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
  const namedCssColor =
    /\b(?:aliceblue|antiquewhite|aqua|aquamarine|azure|beige|bisque|black|blanchedalmond|blue|blueviolet|brown|burlywood|cadetblue|chartreuse|chocolate|coral|cornflowerblue|cornsilk|crimson|cyan|darkblue|darkcyan|darkgoldenrod|darkgray|darkgreen|darkgrey|darkkhaki|darkmagenta|darkolivegreen|darkorange|darkorchid|darkred|darksalmon|darkseagreen|darkslateblue|darkslategray|darkslategrey|darkturquoise|darkviolet|deeppink|deepskyblue|dimgray|dimgrey|dodgerblue|firebrick|floralwhite|forestgreen|fuchsia|gainsboro|ghostwhite|gold|goldenrod|gray|green|greenyellow|grey|honeydew|hotpink|indianred|indigo|ivory|khaki|lavender|lavenderblush|lawngreen|lemonchiffon|lightblue|lightcoral|lightcyan|lightgoldenrodyellow|lightgray|lightgreen|lightgrey|lightpink|lightsalmon|lightseagreen|lightskyblue|lightslategray|lightslategrey|lightsteelblue|lightyellow|lime|limegreen|linen|magenta|maroon|mediumaquamarine|mediumblue|mediumorchid|mediumpurple|mediumseagreen|mediumslateblue|mediumspringgreen|mediumturquoise|mediumvioletred|midnightblue|mintcream|mistyrose|moccasin|navajowhite|navy|oldlace|olive|olivedrab|orange|orangered|orchid|palegoldenrod|palegreen|paleturquoise|palevioletred|papayawhip|peachpuff|peru|pink|plum|powderblue|purple|rebeccapurple|red|rosybrown|royalblue|saddlebrown|salmon|sandybrown|seagreen|seashell|sienna|silver|skyblue|slateblue|slategray|slategrey|snow|springgreen|steelblue|tan|teal|thistle|tomato|turquoise|violet|wheat|white|whitesmoke|yellow|yellowgreen)\b/i;

  if (/(?:^|[^\w])#[\da-f]{3,8}\b/i.test(source)) {
    findings.push("hex color");
  }

  if (
    /\b(?:rgb|rgba|hsl|hsla|hwb|oklch|lab|lch|color|color-mix)\s*\(/i.test(
      source,
    )
  ) {
    findings.push("color function");
  }

  if (namedCssColor.test(source)) {
    findings.push("named color");
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
    /(?:from\s+["'][^"']*palette-primitives|import\s*\(\s*["'][^"']*palette-primitives|require\s*\(\s*["'][^"']*palette-primitives)/i.test(
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

  it("detects named CSS colors without matching Portuguese color labels", () => {
    expect(violations('const styles = { color: "red" };')).toContain(
      "named color",
    );
    expect(violations('const fields = { label: "Cor" };')).not.toContain(
      "named color",
    );
  });

  it("detects color-mix functions", () => {
    expect(
      violations("background: color-mix(in srgb, var(--a), white 20%);"),
    ).toContain("color function");
  });

  it("detects CommonJS palette primitive imports", () => {
    expect(
      violations('const palette = require("../palette-primitives");'),
    ).toContain("private palette primitive import");
  });

  it("keeps literal colors private to the theme primitives", () => {
    const sourceFiles = {
      ...import.meta.glob("../../**/*.{ts,tsx,js,jsx}", {
        eager: true,
        import: "default",
        query: "?raw",
      }),
      ...import.meta.glob("../../../index.html", {
        eager: true,
        import: "default",
        query: "?raw",
      }),
    } as Record<string, string>;
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
