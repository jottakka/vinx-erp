import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
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
          onSubmit={async () => ({ ok: true })}
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
          onSubmit={async () => {
            await create.promise;
            return { ok: true };
          }}
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

  it("reuses the command and confirmed attachment for an exact retry after an ambiguous create result", async () => {
    const user = userEvent.setup();
    const attachmentFileId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    const uploader = {
      upload: vi.fn().mockResolvedValue({ attachmentFileId, ok: true }),
    };
    const onSubmit = vi
      .fn()
      .mockResolvedValueOnce({
        message: "Não foi possível concluir a ação. Tente novamente.",
        ok: false,
      })
      .mockResolvedValueOnce({ ok: true });

    render(
      <VinxThemeProvider>
        <AtendimentoCreateDialog
          assignmentOptions={[]}
          companyId="11111111-1111-4111-8111-111111111111"
          onClose={() => undefined}
          onSubmit={onSubmit}
          open
          uploader={uploader}
        />
      </VinxThemeProvider>,
    );

    await user.type(screen.getByLabelText("Título"), "Pedido com arquivo");
    await user.type(screen.getByLabelText("Descrição"), "Descrição");
    await user.upload(
      screen.getByTestId("atendimento-attachment-picker"),
      new File(["documento"], "evidencia.pdf", { type: "application/pdf" }),
    );
    await user.click(screen.getByRole("button", { name: "Criar cartão" }));
    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    const first = onSubmit.mock.calls[0]?.[0] as {
      attachmentFileId?: string;
      requestId?: string;
    };
    expect(first).toMatchObject({ attachmentFileId });
    expect(first.requestId).toMatch(
      /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i,
    );

    await user.click(screen.getByRole("button", { name: "Criar cartão" }));
    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(2));
    expect(onSubmit.mock.calls[1]?.[0]).toEqual(onSubmit.mock.calls[0]?.[0]);
    expect(uploader.upload).toHaveBeenCalledTimes(1);
  });

  it("rotates a failed create command after a payload change or a new dialog", async () => {
    const user = userEvent.setup();
    const onSubmit = vi.fn().mockResolvedValue({
      message: "Não foi possível concluir a ação. Tente novamente.",
      ok: false,
    });
    const props = {
      assignmentOptions: [],
      companyId: "11111111-1111-4111-8111-111111111111",
      onClose: () => undefined,
      onSubmit,
    };
    const view = render(
      <VinxThemeProvider>
        <AtendimentoCreateDialog {...props} open />
      </VinxThemeProvider>,
    );

    await user.type(screen.getByLabelText("Título"), "Primeiro título");
    await user.type(screen.getByLabelText("Descrição"), "Descrição");
    await user.click(screen.getByRole("button", { name: "Criar cartão" }));
    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    const firstRequest = onSubmit.mock.calls[0]?.[0] as
      | { requestId?: string }
      | undefined;
    if (!firstRequest) throw new Error("Expected first create command");
    const firstRequestId = firstRequest.requestId;

    fireEvent.change(screen.getByLabelText("Título"), {
      target: { value: "Título editado" },
    });
    await user.click(screen.getByRole("button", { name: "Criar cartão" }));
    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(2));
    const editedRequest = onSubmit.mock.calls[1]?.[0] as
      | { requestId?: string }
      | undefined;
    if (!editedRequest) throw new Error("Expected edited create command");
    const editedRequestId = editedRequest.requestId;
    expect(editedRequestId).not.toBe(firstRequestId);

    view.rerender(
      <VinxThemeProvider>
        <AtendimentoCreateDialog {...props} open={false} />
      </VinxThemeProvider>,
    );
    view.rerender(
      <VinxThemeProvider>
        <AtendimentoCreateDialog {...props} open />
      </VinxThemeProvider>,
    );
    await user.type(screen.getByLabelText("Título"), "Novo diálogo");
    await user.type(screen.getByLabelText("Descrição"), "Descrição");
    await user.click(screen.getByRole("button", { name: "Criar cartão" }));
    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(3));
    const newDialogRequest = onSubmit.mock.calls[2]?.[0] as
      | { requestId?: string }
      | undefined;
    if (!newDialogRequest) throw new Error("Expected new dialog command");
    expect(newDialogRequest.requestId).not.toBe(editedRequestId);
  });
});
