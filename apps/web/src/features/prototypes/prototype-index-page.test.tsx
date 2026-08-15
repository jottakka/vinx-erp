import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it } from "vitest";
import { DemoAttachmentField } from "./demo-attachment-field";
import { DetailDrawer } from "./detail-drawer";
import { PrototypeIndexPage } from "./prototype-index-page";

function DemoAttachmentFieldHarness() {
  const [value, setValue] = useState<
    "empty" | "processing" | "ready" | "failed"
  >("empty");

  return (
    <DemoAttachmentField
      label="Comprovante"
      onChange={setValue}
      required
      value={value}
    />
  );
}

function DetailDrawerHarness() {
  const [open, setOpen] = useState(false);

  return (
    <>
      <button onClick={() => setOpen(true)} type="button">
        Abrir detalhes
      </button>
      <DetailDrawer
        onClose={() => setOpen(false)}
        open={open}
        title="Detalhes da solicitação"
      >
        Conteúdo demonstrativo
      </DetailDrawer>
    </>
  );
}

describe("PrototypeIndexPage", () => {
  it.each(["Paguai", "Kanban", "Atendimento", "Pessoas"])(
    "lists the planned %s surface honestly",
    (name) => {
      render(
        <MemoryRouter>
          <PrototypeIndexPage />
        </MemoryRouter>,
      );

      expect(screen.getByRole("heading", { name })).toBeVisible();

      if (name === "Paguai") {
        expect(
          screen.getByRole("link", { name: `Abrir protótipo ${name}` }),
        ).toBeVisible();
        return;
      }

      expect(
        screen.getByText(`${name}: em preparação nesta série`),
      ).toBeVisible();
    },
  );
});

describe("DemoAttachmentField", () => {
  it("names its interactive group from the visible required label", () => {
    render(<DemoAttachmentFieldHarness />);

    const group = screen.getByRole("group", {
      name: "Comprovante (obrigatório)",
    });
    expect(
      within(group).getByRole("button", {
        name: "Adicionar arquivo de demonstração",
      }),
    ).toBeVisible();
  });

  it("advances attachment state only through its explicit control", async () => {
    const user = userEvent.setup();
    render(<DemoAttachmentFieldHarness />);

    await user.click(
      screen.getByRole("button", {
        name: "Adicionar arquivo de demonstração",
      }),
    );
    expect(screen.getByText("Estado demonstrativo: processing")).toBeVisible();

    await user.click(
      screen.getByRole("button", { name: "Avançar estado demonstrativo" }),
    );
    expect(screen.getByText("Estado demonstrativo: ready")).toBeVisible();

    await user.click(
      screen.getByRole("button", { name: "Avançar estado demonstrativo" }),
    );
    expect(screen.getByText("Estado demonstrativo: empty")).toBeVisible();
  });
});

describe("DetailDrawer", () => {
  it("uses its visible title as the dialog name and closes from its button", async () => {
    const user = userEvent.setup();
    render(<DetailDrawerHarness />);

    await user.click(screen.getByRole("button", { name: "Abrir detalhes" }));
    const dialog = screen.getByRole("dialog", {
      name: "Detalhes da solicitação",
    });
    expect(
      within(dialog).getByRole("heading", {
        name: "Detalhes da solicitação",
      }),
    ).toBeVisible();

    await user.click(
      within(dialog).getByRole("button", { name: "Fechar detalhes" }),
    );
    await waitFor(() =>
      expect(
        screen.queryByRole("dialog", { name: "Detalhes da solicitação" }),
      ).not.toBeInTheDocument(),
    );
  });

  it("closes with Escape and restores focus to its opener", async () => {
    const user = userEvent.setup();
    render(<DetailDrawerHarness />);
    const trigger = screen.getByRole("button", { name: "Abrir detalhes" });

    trigger.focus();
    await user.keyboard("{Enter}");
    expect(
      screen.getByRole("dialog", { name: "Detalhes da solicitação" }),
    ).toBeVisible();
    await user.keyboard("{Escape}");

    await waitFor(() =>
      expect(
        screen.queryByRole("dialog", { name: "Detalhes da solicitação" }),
      ).not.toBeInTheDocument(),
    );
    await waitFor(() => expect(trigger).toHaveFocus());
  });
});
