import AttachFileOutlinedIcon from "@mui/icons-material/AttachFileOutlined";
import CloseRoundedIcon from "@mui/icons-material/CloseRounded";
import InsertDriveFileOutlinedIcon from "@mui/icons-material/InsertDriveFileOutlined";
import {
  Box,
  Button,
  Chip,
  CircularProgress,
  IconButton,
  Stack,
  Typography,
} from "@mui/material";
import { type ChangeEvent, useId, useState } from "react";

export type AttachmentFieldState = "idle" | "uploading" | "ready" | "failed";

export type AttachmentFieldValue = {
  fileId: string | null;
  fileName: string;
  mimeType: string;
  sizeBytes: number;
  state: AttachmentFieldState;
};

type AttachmentFieldUploadError = {
  kind: "validation" | "network" | "protocol" | "aborted" | "storage";
  message: string;
};

export type AttachmentUploader = (
  file: File,
  signal: AbortSignal,
) => Promise<
  | {
      ok: true;
      fileId: string;
      fileName: string;
      mimeType: string;
      sizeBytes: number;
    }
  | { ok: false; error: AttachmentFieldUploadError }
>;

type AttachmentFieldProps = {
  accept: string;
  label: string;
  multiple?: boolean;
  onChange?: (values: readonly AttachmentFieldValue[]) => void;
  uploader: AttachmentUploader;
  value: readonly AttachmentFieldValue[];
};

/**
 * Campo de anexo reutilizável: seleciona arquivos, envia pelo uploader injetado
 * e reporta o estado de cada um. Usado pelo formulário do Paguai (comprovante)
 * e compartilhável com outros fluxos que precisem anexar um arquivo.
 */
export function AttachmentField({
  accept,
  label,
  multiple = false,
  onChange,
  uploader,
  value,
}: AttachmentFieldProps) {
  const inputId = useId();
  const [uploading, setUploading] = useState(false);

  function selectFiles(event: ChangeEvent<HTMLInputElement>) {
    const files = Array.from(event.target.files ?? []);
    event.target.value = "";
    if (!files.length || uploading) return;
    void uploadSequence(files);
  }

  async function uploadSequence(files: readonly File[]) {
    const scopeFiles = multiple ? files : files.slice(0, 1);
    setUploading(true);
    const pending = scopeFiles.map((file) => ({
      file,
      fileId: null,
      fileName: file.name,
      mimeType: file.type,
      sizeBytes: file.size,
      state: "uploading" as const,
    }));
    onChange?.([
      ...value.filter((item) => item.state !== "uploading"),
      ...pending.map((item) => ({
        fileId: item.fileId,
        fileName: item.fileName,
        mimeType: item.mimeType,
        sizeBytes: item.sizeBytes,
        state: item.state as AttachmentFieldState,
      })),
    ]);

    const results = await Promise.all(
      pending.map(async (entry) => {
        const controller = new AbortController();
        const result = await uploader(entry.file, controller.signal);
        return { entry, result };
      }),
    );

    onChange?.([
      ...value.filter((item) => item.state !== "uploading"),
      ...results.map(({ entry, result }) =>
        result.ok
          ? {
              fileId: result.fileId,
              fileName: result.fileName,
              mimeType: result.mimeType,
              sizeBytes: result.sizeBytes,
              state: "ready" as const,
            }
          : {
              fileId: null,
              fileName: entry.fileName,
              mimeType: entry.mimeType,
              sizeBytes: entry.sizeBytes,
              state: "failed" as const,
            },
      ),
    ]);
    setUploading(false);
  }

  function remove(entry: AttachmentFieldValue) {
    onChange?.(value.filter((item) => item.fileName !== entry.fileName));
  }

  return (
    <Box component="fieldset" sx={{ border: 0, m: 0, minWidth: 0, p: 0 }}>
      <Stack spacing={1}>
        <Stack direction="row" spacing={1} sx={{ alignItems: "center" }}>
          <Typography component="legend" sx={{ p: 0 }} variant="subtitle2">
            {label}
          </Typography>
          <Button
            component="label"
            disabled={uploading}
            size="small"
            startIcon={<AttachFileOutlinedIcon />}
          >
            Anexar
            <input
              accept={accept}
              aria-label={`${label}: anexar arquivo`}
              disabled={uploading}
              id={inputId}
              multiple={multiple}
              onChange={selectFiles}
              style={{ display: "none" }}
              type="file"
            />
          </Button>
        </Stack>
        {value.length ? (
          <Stack spacing={0.5}>
            {value.map((entry) => (
              <Stack
                direction="row"
                key={`${entry.fileName}-${entry.sizeBytes}`}
                spacing={1}
                sx={{ alignItems: "center" }}
              >
                <InsertDriveFileOutlinedIcon color="action" fontSize="small" />
                <Typography sx={{ flexGrow: 1 }} variant="body2">
                  {entry.fileName}
                </Typography>
                <Chip label={attachmentStateText(entry.state)} size="small" />
                {entry.state === "uploading" ? (
                  <CircularProgress aria-hidden size={16} />
                ) : null}
                <IconButton
                  aria-label={`Remover ${entry.fileName}`}
                  onClick={() => remove(entry)}
                  size="small"
                >
                  <CloseRoundedIcon fontSize="small" />
                </IconButton>
              </Stack>
            ))}
          </Stack>
        ) : null}
      </Stack>
    </Box>
  );
}

function attachmentStateText(state: AttachmentFieldState): string {
  switch (state) {
    case "idle":
      return "Aguardando";
    case "uploading":
      return "Enviando";
    case "ready":
      return "Anexado";
    case "failed":
      return "Falhou";
  }
}
