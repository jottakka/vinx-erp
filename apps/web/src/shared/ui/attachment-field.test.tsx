import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { describe, expect, it, vi } from "vitest";
import {
  AttachmentField,
  type AttachmentFieldValue,
  type AttachmentUploader,
} from "./attachment-field";

type FieldHostProps = {
  multiple?: boolean;
  onChange?: (values: readonly AttachmentFieldValue[]) => void;
  uploader: AttachmentUploader;
};

function FieldHost({ multiple = false, onChange, uploader }: FieldHostProps) {
  const [value, setValue] = useState<readonly AttachmentFieldValue[]>([]);

  return (
    <AttachmentField
      accept="application/pdf"
      label="Comprovante"
      multiple={multiple}
      onChange={(next) => {
        setValue(next);
        onChange?.(next);
      }}
      uploader={uploader}
      value={value}
    />
  );
}

function renderField(props: FieldHostProps) {
  render(<FieldHost {...props} />);
  return screen.getByLabelText("Comprovante: anexar arquivo");
}

function okUploader(
  fileIdFor: (attempt: number) => string,
): AttachmentUploader {
  let attempt = 0;
  return vi.fn(async (file: File) => ({
    ok: true as const,
    fileId: fileIdFor(attempt++),
    fileName: file.name,
    mimeType: file.type,
    sizeBytes: file.size,
  }));
}

describe("AttachmentField", () => {
  it("replaces the previous file when a new one is selected in singular mode", async () => {
    const user = userEvent.setup();
    const input = renderField({
      uploader: okUploader((attempt) => `file-${attempt}`),
    });

    await user.upload(
      input,
      new File(["a"], "fatura-a.pdf", { type: "application/pdf" }),
    );
    await waitFor(() =>
      expect(screen.getByText("Anexado")).toBeInTheDocument(),
    );
    await user.upload(
      input,
      new File(["b"], "fatura-b.pdf", { type: "application/pdf" }),
    );

    await waitFor(() =>
      expect(screen.getByText("fatura-b.pdf")).toBeInTheDocument(),
    );
    // Modo singular: apenas o anexo mais recente permanece no campo.
    expect(screen.queryByText("fatura-a.pdf")).not.toBeInTheDocument();
    expect(screen.getAllByText("Anexado")).toHaveLength(1);
  });

  it("keeps every file in multiple mode", async () => {
    const user = userEvent.setup();
    const input = renderField({
      multiple: true,
      uploader: okUploader((attempt) => `file-${attempt}`),
    });

    await user.upload(
      input,
      new File(["a"], "fatura-a.pdf", { type: "application/pdf" }),
    );
    await waitFor(() =>
      expect(screen.getByText("Anexado")).toBeInTheDocument(),
    );
    await user.upload(
      input,
      new File(["b"], "fatura-b.pdf", { type: "application/pdf" }),
    );

    await waitFor(() =>
      expect(screen.getByText("fatura-b.pdf")).toBeInTheDocument(),
    );
    expect(screen.getByText("fatura-a.pdf")).toBeInTheDocument();
    expect(screen.getAllByText("Anexado")).toHaveLength(2);
  });

  it("passes the uploader-provided file id to the reported value", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    const input = renderField({
      onChange,
      uploader: okUploader(() => "file-canonical"),
    });

    await user.upload(
      input,
      new File(["a"], "fatura.pdf", { type: "application/pdf" }),
    );

    await waitFor(() => {
      const latest = onChange.mock.calls.at(-1)?.[0] ?? [];
      expect(latest[0]).toMatchObject({
        fileId: "file-canonical",
        state: "ready",
      });
    });
  });
});
