import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { PrototypeIndexPage } from "./prototype-index-page";

describe("PrototypeIndexPage", () => {
  it.each(["Paguai", "Kanban", "Atendimento", "Pessoas"])(
    "lists the planned %s surface honestly",
    (name) => {
      render(<PrototypeIndexPage />);

      expect(screen.getByRole("heading", { name })).toBeVisible();
      expect(
        screen.getByText(`${name}: em preparação nesta série`),
      ).toBeVisible();
    },
  );
});
