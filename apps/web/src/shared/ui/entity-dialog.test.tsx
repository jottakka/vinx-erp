import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { EntityDialog } from "./entity-dialog";

function renderDialog(onClose = vi.fn()) {
  render(
    <EntityDialog
      description="Preencha os campos abaixo."
      onClose={onClose}
      open
      title="Novo rascunho"
    >
      <input aria-label="valor" />
    </EntityDialog>,
  );
  return onClose;
}

describe("EntityDialog", () => {
  it("labels the dialog with the title heading and renders a single h2", async () => {
    renderDialog();

    const dialog = await screen.findByRole("dialog", { name: "Novo rascunho" });
    expect(dialog).toBeVisible();

    const headings = screen.getAllByRole("heading", { name: "Novo rascunho" });
    expect(headings).toHaveLength(1);
    expect(headings[0].tagName).toBe("H2");

    const describedBy = dialog.getAttribute("aria-describedby");
    expect(describedBy).toBeTruthy();
    expect(document.getElementById(describedBy!)).toHaveTextContent(
      "Preencha os campos abaixo.",
    );
  });

  it("keeps focus trapping and closes through the accessible close button", async () => {
    const onClose = renderDialog();
    const user = userEvent.setup();

    await user.type(screen.getByLabelText("valor"), "12");
    expect(screen.getByLabelText("valor")).toHaveValue("12");

    await user.click(screen.getByRole("button", { name: "Fechar" }));
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});
