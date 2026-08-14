import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { FeedbackProvider } from "../../../shared/ui/feedback-provider";
import { PaguaiPrototypePage } from "./paguai-prototype-page";

describe("PaguaiPrototypePage", () => {
  it("exposes named tab and panel semantics for each local section", async () => {
    const user = userEvent.setup();
    render(
      <FeedbackProvider>
        <PaguaiPrototypePage />
      </FeedbackProvider>,
    );

    const tablist = screen.getByRole("tablist", {
      name: "Seções do Paguai",
    });
    const requestsTab = within(tablist).getByRole("tab", {
      name: "Solicitações",
    });
    expect(requestsTab).toHaveAttribute(
      "aria-controls",
      "paguai-panel-requests",
    );
    expect(
      screen.getByRole("tabpanel", { name: "Solicitações" }),
    ).toHaveAttribute("id", "paguai-panel-requests");

    await user.click(within(tablist).getByRole("tab", { name: "Financeiro" }));
    expect(
      screen.getByRole("tabpanel", { name: "Financeiro" }),
    ).toHaveAttribute("id", "paguai-panel-finance");
  });

  it("requires an explicit company before creating a local draft", async () => {
    const user = userEvent.setup();
    render(
      <FeedbackProvider>
        <PaguaiPrototypePage />
      </FeedbackProvider>,
    );

    const opener = screen.getByRole("button", { name: "Nova solicitação" });
    await user.click(opener);
    await user.click(screen.getByRole("button", { name: "Cancelar" }));
    await waitFor(() => expect(opener).toHaveFocus());

    await user.click(opener);
    await user.click(screen.getByRole("button", { name: "Salvar rascunho" }));
    const company = screen.getByRole("combobox", { name: "Empresa" });
    const companyError = screen.getByText("Selecione uma empresa");
    expect(companyError).toHaveAttribute("id", "paguai-company-error");
    expect(company).toHaveAttribute("aria-describedby", "paguai-company-error");
    expect(company).toHaveFocus();

    await user.click(company);
    await user.click(screen.getByRole("option", { name: "Alfa" }));
    await user.type(screen.getByLabelText("Descrição"), "Licença anual");
    await user.type(screen.getByLabelText("Valor"), "1200");
    await user.click(screen.getByRole("button", { name: "Salvar rascunho" }));

    expect(screen.getByText("Licença anual")).toBeVisible();
    expect(screen.getByText("Alteração local de demonstração")).toBeVisible();
  });

  it("keeps request-list filters out of Conference and Finance", async () => {
    const user = userEvent.setup();
    render(
      <FeedbackProvider>
        <PaguaiPrototypePage />
      </FeedbackProvider>,
    );

    await user.click(
      screen.getByRole("combobox", { name: "Filtrar por empresa" }),
    );
    await user.click(screen.getByRole("option", { name: "Beta" }));
    expect(screen.getByText("Serviço de manutenção")).toBeVisible();
    expect(screen.getByText("Consultoria tributária")).toBeVisible();
    expect(screen.queryByText("Renovação de licença")).not.toBeInTheDocument();

    const statusFilter = screen.getByRole("combobox", {
      name: "Filtrar por status",
    });
    statusFilter.focus();
    await user.keyboard("{Enter}fin{Enter}");
    expect(statusFilter).toHaveTextContent("Financeiro");
    expect(screen.getByText("Serviço de manutenção")).toBeVisible();
    expect(
      screen.queryByText("Consultoria tributária"),
    ).not.toBeInTheDocument();

    await user.click(screen.getByRole("tab", { name: "Conferência" }));
    expect(screen.getByText("Renovação de licença")).toBeVisible();
    expect(screen.getByText("Consultoria tributária")).toBeVisible();

    await user.click(screen.getByRole("tab", { name: "Financeiro" }));
    expect(screen.getByText("Serviço de manutenção")).toBeVisible();
  });

  it("returns a concrete company request and preserves its local reason", async () => {
    const user = userEvent.setup();
    render(
      <FeedbackProvider>
        <PaguaiPrototypePage />
      </FeedbackProvider>,
    );

    await user.click(screen.getByRole("tab", { name: "Conferência" }));
    await user.click(
      screen.getByRole("button", {
        name: "Selecionar Consultoria tributária da empresa Beta",
      }),
    );
    const selectedRequest = screen.getByRole("region", {
      name: "Solicitação selecionada",
    });
    expect(within(selectedRequest).getByText("Empresa: Beta")).toBeVisible();
    const returnTrigger = within(selectedRequest).getByRole("button", {
      name: "Devolver Consultoria tributária para ajuste",
    });

    await user.click(returnTrigger);
    await user.click(screen.getByRole("button", { name: "Cancelar" }));
    await waitFor(() => expect(returnTrigger).toHaveFocus());

    const detachedOpenerFocus = vi.spyOn(returnTrigger, "focus");
    await user.click(returnTrigger);
    detachedOpenerFocus.mockClear();
    await user.click(
      screen.getByRole("button", { name: "Confirmar devolução" }),
    );
    const dialog = screen.getByRole("dialog", { name: "Devolver solicitação" });
    const category = within(dialog).getByRole("combobox", {
      name: "Categoria",
    });
    const categoryError = within(dialog).getByText("Selecione uma categoria");
    expect(categoryError).toHaveAttribute("id", "paguai-return-category-error");
    expect(category).toHaveAttribute(
      "aria-describedby",
      "paguai-return-category-error",
    );
    expect(category).toHaveFocus();
    expect(
      within(dialog).getByText("Descreva o motivo da devolução"),
    ).toBeVisible();

    await user.click(category);
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
    expect(within(selectedRequest).getByText("Empresa: Beta")).toBeVisible();
    expect(
      within(selectedRequest).getByText("Categoria: Dados incorretos"),
    ).toBeVisible();
    expect(
      within(selectedRequest).getByText("Motivo: Anexe a nota fiscal correta"),
    ).toBeVisible();
    expect(selectedRequest).toHaveAttribute("tabindex", "-1");
    await waitFor(() => {
      expect(
        screen.queryByRole("dialog", { name: "Devolver solicitação" }),
      ).not.toBeInTheDocument();
      expect(selectedRequest).toHaveFocus();
    });
    expect(returnTrigger).not.toBeInTheDocument();
    expect(detachedOpenerFocus).not.toHaveBeenCalled();
    detachedOpenerFocus.mockRestore();
  });

  it("guards payment confirmation until the proof reaches ready", async () => {
    const user = userEvent.setup();
    render(
      <FeedbackProvider>
        <PaguaiPrototypePage />
      </FeedbackProvider>,
    );

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
    render(
      <FeedbackProvider>
        <PaguaiPrototypePage />
      </FeedbackProvider>,
    );

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
