import { fireEvent, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { VinxThemeProvider } from "../../app/theme/vinx-theme-provider";
import { PaguaiCreateDialog } from "./paguai-create-dialog";

describe("PaguaiCreateDialog", () => {
  it("shows a Portuguese title limit instead of the default Zod message", async () => {
    render(
      <VinxThemeProvider>
        <PaguaiCreateDialog
          company={{
            companyId: "11111111-1111-4111-8111-111111111111",
            companyName: "Alfa",
          }}
          onClose={() => undefined}
          onSubmit={() => undefined}
          open
          uploader={async () => ({
            error: { kind: "network" as const, message: "Falha" },
            ok: false as const,
          })}
        />
      </VinxThemeProvider>,
    );

    fireEvent.change(screen.getByLabelText("Título"), {
      target: { value: "x".repeat(201) },
    });
    const form = screen.getByLabelText("Título").closest("form");
    if (!form) throw new Error("Expected create form");
    fireEvent.submit(form);

    expect(
      await screen.findByText("O título deve ter no máximo 200 caracteres."),
    ).toBeVisible();
    expect(screen.queryByText(/String must contain/)).not.toBeInTheDocument();
  });

  it("blocks creation when the selected invoice failed and explains the recovery", async () => {
    const user = userEvent.setup();
    const onSubmit = vi.fn();
    let uploadAttempts = 0;
    render(
      <VinxThemeProvider>
        <PaguaiCreateDialog
          company={{
            companyId: "11111111-1111-4111-8111-111111111111",
            companyName: "Alfa",
          }}
          onClose={() => undefined}
          onSubmit={onSubmit}
          open
          uploader={async (file) => {
            uploadAttempts += 1;
            return uploadAttempts === 1
              ? {
                  error: { kind: "network" as const, message: "Falha" },
                  ok: false as const,
                }
              : {
                  fileId: "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
                  fileName: file.name,
                  mimeType: file.type,
                  ok: true as const,
                  sizeBytes: file.size,
                };
          }}
        />
      </VinxThemeProvider>,
    );

    await user.type(screen.getByLabelText("Título"), "Nova solicitação");
    await user.type(screen.getByLabelText("Descrição"), "Descrição");
    await user.type(screen.getByLabelText("Valor (R$)"), "1.250,00");
    const dueDate = document.querySelector('input[name="dueDate"]');
    if (!dueDate) throw new Error("Expected due date input");
    fireEvent.change(dueDate, {
      target: { value: "2026-09-01" },
    });
    await user.type(screen.getByLabelText("Quem receberá"), "Fornecedor");
    await user.click(
      screen.getByRole("combobox", { name: "Comprovante de pagamento" }),
    );
    await user.click(
      await screen.findByRole("option", { name: "Obrigatório" }),
    );
    await user.upload(
      screen.getByLabelText(
        "Fatura ou documento da solicitação: anexar arquivo",
      ),
      new File(["fatura"], "fatura.pdf", { type: "application/pdf" }),
    );

    expect(await screen.findByText("Falhou")).toBeVisible();
    expect(
      await screen.findByText(
        "O anexo selecionado não foi enviado. Remova-o e selecione o arquivo novamente antes de criar a solicitação.",
      ),
    ).toBeVisible();
    const form = screen.getByLabelText("Título").closest("form");
    if (!form) throw new Error("Expected create form");
    fireEvent.submit(form);
    expect(onSubmit).not.toHaveBeenCalled();
    expect(
      screen.getByRole("button", { name: "Criar rascunho" }),
    ).toBeDisabled();
    expect(
      screen.getByRole("button", { name: "Remover fatura.pdf" }),
    ).toBeVisible();

    await user.click(
      screen.getByRole("button", { name: "Remover fatura.pdf" }),
    );
    await user.upload(
      screen.getByLabelText(
        "Fatura ou documento da solicitação: anexar arquivo",
      ),
      new File(["fatura nova"], "fatura-nova.pdf", {
        type: "application/pdf",
      }),
    );
    expect(await screen.findByText("Anexado")).toBeVisible();
    await user.click(screen.getByRole("button", { name: "Criar rascunho" }));

    expect(onSubmit).toHaveBeenCalledWith(
      expect.objectContaining({
        invoiceFileId: "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
      }),
    );
  });
});
