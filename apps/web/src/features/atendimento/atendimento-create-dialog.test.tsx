import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
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
  it("submits the form defaults for operational metadata", async () => {
    const user = userEvent.setup();
    const onSubmit = vi.fn().mockResolvedValue({ ok: true });
    render(
      <VinxThemeProvider>
        <AtendimentoCreateDialog
          assignmentOptions={[]}
          companyId="11111111-1111-4111-8111-111111111111"
          onClose={() => undefined}
          onSubmit={onSubmit}
          open
        />
      </VinxThemeProvider>,
    );

    await user.type(screen.getByLabelText("Título"), "Atendimento padrão");
    await user.type(screen.getByLabelText("Descrição"), "Descrição");
    await user.click(screen.getByRole("button", { name: "Criar cartão" }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(onSubmit.mock.calls[0]?.[0]).toMatchObject({
      priority: "media",
      channel: "web",
      slaDueAt: null,
    });
  });

  it("submits explicit priority, channel, and UTC SLA timestamp", async () => {
    const user = userEvent.setup();
    const onSubmit = vi.fn().mockResolvedValue({ ok: true });
    const localDateTime = "2026-08-28T18:00";
    render(
      <VinxThemeProvider>
        <AtendimentoCreateDialog
          assignmentOptions={[]}
          companyId="11111111-1111-4111-1111-111111111111"
          onClose={() => undefined}
          onSubmit={onSubmit}
          open
        />
      </VinxThemeProvider>,
    );

    await user.type(screen.getByLabelText("Título"), "Atendimento urgente");
    await user.type(screen.getByLabelText("Descrição"), "Descrição");
    await user.click(screen.getByRole("combobox", { name: "Prioridade" }));
    await user.click(screen.getByRole("option", { name: "Alta" }));
    await user.click(screen.getByRole("combobox", { name: "Canal" }));
    await user.click(screen.getByRole("option", { name: "WhatsApp" }));
    fireEvent.change(screen.getByLabelText("Prazo de SLA"), {
      target: { value: localDateTime },
    });
    await user.click(screen.getByRole("button", { name: "Criar cartão" }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(onSubmit.mock.calls[0]?.[0]).toMatchObject({
      priority: "alta",
      channel: "whatsapp",
      slaDueAt: new Date(localDateTime).toISOString(),
    });
  });

  it("labels the SLA field as an operational deadline without scheduling an action", () => {
    render(
      <VinxThemeProvider>
        <AtendimentoCreateDialog
          assignmentOptions={[]}
          companyId="11111111-1111-4111-1111-111111111111"
          onClose={() => undefined}
          onSubmit={async () => ({ ok: true })}
          open
        />
      </VinxThemeProvider>,
    );

    expect(screen.getByLabelText("Prazo de SLA")).toBeVisible();
    expect(
      screen.getByText("Data limite operacional; não agenda uma ação."),
    ).toBeVisible();
  });

  it("opens its file picker from a focusable attachment button", async () => {
    const user = userEvent.setup();
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

    const picker = screen.getByTestId("atendimento-attachment-picker");
    const pickerClick = vi.spyOn(picker, "click");
    await user.click(
      screen.getByRole("button", { name: "Anexar arquivo (opcional)" }),
    );
    expect(pickerClick).toHaveBeenCalledTimes(1);
  });

  it("resets company-scoped people and the failed attempt when the dialog company changes", async () => {
    const user = userEvent.setup();
    const onSubmit = vi.fn().mockResolvedValue({
      message: "Não foi possível concluir a ação. Tente novamente.",
      ok: false,
    });
    const companies = [
      {
        companyId: "11111111-1111-4111-8111-111111111111",
        companyName: "Alfa",
      },
      {
        companyId: "22222222-2222-4222-8222-222222222222",
        companyName: "Beta",
      },
    ];

    function SwitchingDialog() {
      const [companyId, setCompanyId] = useState(companies[0].companyId);
      const assignmentOptions =
        companyId === companies[0].companyId
          ? [
              {
                displayName: "Maria Silva",
                membershipId: "33333333-3333-4333-8333-333333333333",
              },
            ]
          : [
              {
                displayName: "Pedro Souza",
                membershipId: "44444444-4444-8444-8444-444444444444",
              },
            ];
      return (
        <AtendimentoCreateDialog
          assignmentOptions={assignmentOptions}
          companies={companies}
          companyId={companyId}
          onClose={() => undefined}
          onCompanyChange={setCompanyId}
          onSubmit={onSubmit}
          open
        />
      );
    }

    render(
      <VinxThemeProvider>
        <SwitchingDialog />
      </VinxThemeProvider>,
    );
    await user.type(screen.getByLabelText("Título"), "Atendimento Alfa");
    await user.type(screen.getByLabelText("Descrição"), "Descrição Alfa");
    await user.click(screen.getByRole("button", { name: "Criar cartão" }));
    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    const alfaRequestId = onSubmit.mock.calls[0]?.[0]?.requestId;

    await user.click(
      screen.getByRole("combobox", { name: "Empresa do atendimento" }),
    );
    await user.click(await screen.findByRole("option", { name: /Beta/ }));

    expect(screen.getByLabelText("Título")).toHaveValue("");
    expect(screen.getByLabelText("Descrição")).toHaveValue("");
    await user.click(
      screen.getByRole("combobox", { name: "Pessoas envolvidas" }),
    );
    expect(
      await screen.findByRole("option", { name: "Pedro Souza" }),
    ).toBeVisible();
    expect(screen.queryByRole("option", { name: "Maria Silva" })).toBeNull();
    await user.keyboard("{Escape}");

    await user.type(screen.getByLabelText("Título"), "Atendimento Beta");
    await user.type(screen.getByLabelText("Descrição"), "Descrição Beta");
    await user.click(screen.getByRole("button", { name: "Criar cartão" }));
    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(2));
    expect(onSubmit.mock.calls[1]?.[0]?.requestId).not.toBe(alfaRequestId);
  });

  it("uses the medium full-width dialog with responsive form and action regions", () => {
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

    const dialog = screen.getByRole("dialog");
    expect(dialog).toHaveClass("MuiDialog-paperFullWidth");
    expect(dialog).toHaveClass("MuiDialog-paperWidthMd");
    const companyField = screen.getByTestId("atendimento-create-company-field");
    expect(companyField).toHaveStyle({ flex: "1 1 0%", minWidth: "0px" });
    expect(companyField.querySelector(".MuiFormControl-root")).toHaveStyle({
      width: "100%",
    });
    expect(
      within(dialog).getByRole("button", { name: "Cancelar" }),
    ).toBeVisible();
    expect(
      within(dialog).getByRole("button", { name: "Criar cartão" }),
    ).toBeVisible();
  });

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

  it.each([
    ["priority", "Prioridade", "Alta"],
    ["channel", "Canal", "WhatsApp"],
  ] as const)(
    "rotates a pending retry when %s changes",
    async (_field, label, option) => {
      const user = userEvent.setup();
      const onSubmit = vi.fn().mockResolvedValue({
        message: "Não foi possível concluir a ação. Tente novamente.",
        ok: false,
      });
      render(
        <VinxThemeProvider>
          <AtendimentoCreateDialog
            assignmentOptions={[]}
            companyId="11111111-1111-4111-1111-111111111111"
            onClose={() => undefined}
            onSubmit={onSubmit}
            open
          />
        </VinxThemeProvider>,
      );

      await user.type(screen.getByLabelText("Título"), "Pedido");
      await user.type(screen.getByLabelText("Descrição"), "Descrição");
      await user.click(screen.getByRole("button", { name: "Criar cartão" }));
      await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
      const firstRequestId = onSubmit.mock.calls[0]?.[0]?.requestId;

      await user.click(screen.getByRole("combobox", { name: label }));
      await user.click(screen.getByRole("option", { name: option }));
      await user.click(screen.getByRole("button", { name: "Criar cartão" }));
      await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(2));
      expect(onSubmit.mock.calls[1]?.[0]?.requestId).not.toBe(firstRequestId);
    },
  );
});
