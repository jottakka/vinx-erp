import { fireEvent, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { VinxThemeProvider } from "../../app/theme/vinx-theme-provider";
import { AtendimentoCreateDialog } from "./atendimento-create-dialog";

function deferred<T>() {
  let resolve!: (value: T) => void;
  return {
    promise: new Promise<T>((complete) => {
      resolve = complete;
    }),
    resolve,
  };
}

describe("AtendimentoCreateDialog", () => {
  it("shows a Portuguese title limit instead of the default Zod message", async () => {
    render(
      <VinxThemeProvider>
        <AtendimentoCreateDialog
          assignmentOptions={[]}
          companyId="11111111-1111-4111-8111-111111111111"
          onClose={() => undefined}
          onSubmit={async () => undefined}
          open
        />
      </VinxThemeProvider>,
    );

    const title = screen.getByLabelText("Título");
    fireEvent.change(title, { target: { value: "x".repeat(201) } });
    const form = title.closest("form");
    if (!form) throw new Error("Expected create form");
    fireEvent.submit(form);

    expect(
      await screen.findByText("O título deve ter no máximo 200 caracteres."),
    ).toBeVisible();
    expect(screen.queryByText(/String must contain/)).not.toBeInTheDocument();
  });

  it("shows separate attachment and creation phases while locking the form", async () => {
    const user = userEvent.setup();
    const upload = deferred<{
      ok: true;
      attachmentFileId: string;
    }>();
    const create = deferred<void>();

    render(
      <VinxThemeProvider>
        <AtendimentoCreateDialog
          assignmentOptions={[]}
          companyId="11111111-1111-4111-8111-111111111111"
          onClose={() => undefined}
          onSubmit={() => create.promise}
          open
          uploader={{ upload: () => upload.promise }}
        />
      </VinxThemeProvider>,
    );

    await user.type(screen.getByLabelText("Título"), "Novo atendimento");
    await user.type(screen.getByLabelText("Descrição"), "Descrição");
    await user.upload(
      screen.getByTestId("atendimento-attachment-picker"),
      new File(["documento"], "evidencia.pdf", { type: "application/pdf" }),
    );
    await user.click(screen.getByRole("button", { name: "Criar cartão" }));

    expect(
      await screen.findByRole("button", { name: "Anexando…" }),
    ).toBeDisabled();
    expect(screen.getByRole("status")).toHaveTextContent("Anexando arquivo…");
    expect(screen.getByLabelText("Título")).toBeDisabled();
    expect(screen.getByRole("button", { name: "Cancelar" })).toBeDisabled();

    upload.resolve({
      attachmentFileId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
      ok: true,
    });
    expect(
      await screen.findByRole("button", { name: "Criando…" }),
    ).toBeDisabled();
    expect(screen.getByRole("status")).toHaveTextContent(
      "Criando atendimento…",
    );
    expect(screen.getByLabelText("Descrição")).toBeDisabled();

    create.resolve();
  });
});
