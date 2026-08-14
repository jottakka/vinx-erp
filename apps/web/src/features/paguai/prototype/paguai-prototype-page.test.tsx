import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { PaguaiPrototypePage } from "./paguai-prototype-page";

describe("PaguaiPrototypePage", () => {
  it("requires an explicit company before creating a local draft", async () => {
    const user = userEvent.setup();
    render(<PaguaiPrototypePage />);

    await user.click(screen.getByRole("button", { name: "Nova solicitação" }));
    await user.click(screen.getByRole("button", { name: "Salvar rascunho" }));
    expect(screen.getByText("Selecione uma empresa")).toBeVisible();

    await user.click(screen.getByRole("combobox", { name: "Empresa" }));
    await user.click(screen.getByRole("option", { name: "Alfa" }));
    await user.type(screen.getByLabelText("Descrição"), "Licença anual");
    await user.type(screen.getByLabelText("Valor"), "1200");
    await user.click(screen.getByRole("button", { name: "Salvar rascunho" }));

    expect(screen.getByText("Licença anual")).toBeVisible();
    expect(screen.getByText("Alteração local de demonstração")).toBeVisible();
  });

  it("requires category and description before returning a conference request", async () => {
    const user = userEvent.setup();
    render(<PaguaiPrototypePage />);

    await user.click(screen.getByRole("tab", { name: "Conferência" }));
    await user.click(
      screen.getByRole("button", { name: "Devolver para ajuste" }),
    );
    const dialog = screen.getByRole("dialog", { name: "Devolver solicitação" });

    await user.click(
      within(dialog).getByRole("button", { name: "Confirmar devolução" }),
    );
    expect(within(dialog).getByText("Selecione uma categoria")).toBeVisible();
    expect(
      within(dialog).getByText("Descreva o motivo da devolução"),
    ).toBeVisible();

    await user.click(
      within(dialog).getByRole("combobox", { name: "Categoria" }),
    );
    await user.click(screen.getByRole("option", { name: "Dados incorretos" }));
    await user.type(
      within(dialog).getByLabelText("Descrição da devolução"),
      "Anexe a nota fiscal correta",
    );
    await user.click(
      within(dialog).getByRole("button", { name: "Confirmar devolução" }),
    );

    expect(
      screen.getByText("Solicitação devolvida para ajustes"),
    ).toBeVisible();
  });

  it("guards payment confirmation until the proof reaches ready", async () => {
    const user = userEvent.setup();
    render(<PaguaiPrototypePage />);

    await user.click(screen.getByRole("tab", { name: "Financeiro" }));
    const confirm = screen.getByRole("button", { name: "Confirmar pagamento" });
    expect(confirm).toBeDisabled();

    await user.click(
      screen.getByRole("button", { name: "Adicionar arquivo de demonstração" }),
    );
    await user.click(
      screen.getByRole("button", { name: "Avançar estado demonstrativo" }),
    );
    expect(screen.getByText("Estado demonstrativo: ready")).toBeVisible();
    expect(confirm).toBeEnabled();

    await user.click(confirm);
    expect(screen.getByText("Pagamento confirmado localmente")).toBeVisible();
  });

  it("edits policy audience locally and keeps the simulation boundary visible", async () => {
    const user = userEvent.setup();
    render(<PaguaiPrototypePage />);

    expect(screen.getByText("Dados de demonstração")).toBeVisible();
    await user.click(screen.getByRole("tab", { name: "Políticas" }));
    await user.click(
      screen.getByRole("combobox", { name: "Audiência da política" }),
    );
    await user.click(screen.getByRole("option", { name: "Gestores" }));

    expect(screen.getByText("Audiência local: Gestores")).toBeVisible();
    expect(screen.getByText("Simulação sem efeito real")).toBeVisible();
    expect(screen.getByText("Dados de demonstração")).toBeVisible();
  });
});
