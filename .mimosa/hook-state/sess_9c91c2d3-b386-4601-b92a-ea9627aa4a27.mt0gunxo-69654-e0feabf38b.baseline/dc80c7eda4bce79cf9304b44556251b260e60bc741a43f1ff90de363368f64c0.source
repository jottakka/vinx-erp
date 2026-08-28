import { render, screen } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { beforeEach } from "vitest";
import { server } from "../test/msw/server";
import { AppProviders } from "./providers";
import { VinxThemeProvider } from "./theme/vinx-theme-provider";

beforeEach(() => {
  server.use(
    http.get(
      "/foundation/bootstrap",
      () => new HttpResponse(null, { status: 401 }),
    ),
  );
});

it("renders application content within the shell", () => {
  render(
    <AppProviders>
      <p>Vinx shell</p>
    </AppProviders>,
  );

  expect(screen.getByText("Vinx shell")).toBeInTheDocument();
});

it("applies semantic document baseline styles", () => {
  render(
    <VinxThemeProvider>
      <p>Vinx shell</p>
    </VinxThemeProvider>,
  );

  const globalStyles = [
    ...document.querySelectorAll('style[data-emotion="css-global"]'),
  ]
    .map((style) => style.textContent)
    .join("\n");

  expect(globalStyles).toContain("body{margin:0");
  expect(globalStyles).toContain(
    "background-color:var(--vinx-palette-background-default)",
  );
  expect(globalStyles).toContain("color-scheme:light");
});
