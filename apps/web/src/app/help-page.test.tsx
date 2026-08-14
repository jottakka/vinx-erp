import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it } from "vitest";
import { HelpPage } from "./help-page";

describe("HelpPage", () => {
  it("renders only navigation links injected by its app-layer caller", () => {
    render(
      <MemoryRouter>
        <HelpPage
          navigationLinks={[
            {
              group: "modules",
              label: "Paguai",
              navigation: true,
              path: "/paguai",
            },
          ]}
        />
      </MemoryRouter>,
    );

    expect(screen.getByRole("link", { name: "Paguai" })).toHaveAttribute(
      "href",
      "/paguai",
    );
    expect(
      screen.queryByRole("link", { name: "Ajuda" }),
    ).not.toBeInTheDocument();
  });
});
