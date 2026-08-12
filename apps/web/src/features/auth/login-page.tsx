import { Alert, Button, Stack, TextField, Typography } from "@mui/material";
import { type FormEvent, useRef, useState } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import { resolveAppPath } from "../../app/app-navigation";
import { PublicLayout } from "../../app/public-layout";
import { useSession } from "./session-provider";

const profileErrorId = "development-profile-error";

function returnPathFromState(state: unknown) {
  return state && typeof state === "object"
    ? resolveAppPath(Reflect.get(state, "returnPath"))
    : "/";
}

export function LoginPage() {
  const [profileId, setProfileId] = useState("maria");
  const [validationError, setValidationError] = useState<string | null>(null);
  const profileInput = useRef<HTMLInputElement>(null);
  const { error, signIn, status } = useSession();
  const location = useLocation();
  const navigate = useNavigate();
  const visibleError = validationError ?? error;
  const returnPath = returnPathFromState(location.state);
  const pending = status === "pending";

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const normalizedProfileId = profileId.trim();

    if (!normalizedProfileId) {
      setValidationError("Informe um perfil");
      profileInput.current?.focus();
      return;
    }

    setValidationError(null);
    if (await signIn(normalizedProfileId)) {
      navigate(returnPath, { replace: true });
      return;
    }

    profileInput.current?.focus();
  }

  return (
    <PublicLayout>
      <Stack sx={(theme) => ({ gap: `${theme.vinx.layout.contentGap}px` })}>
        <Stack spacing={1}>
          <Typography component="h1" variant="h4">
            Entrada do ambiente de desenvolvimento
          </Typography>
          <Typography color="text.secondary" component="p">
            Use o perfil de desenvolvimento autorizado para validar a interface.
          </Typography>
          <Typography color="text.secondary" component="p" variant="body2">
            Entrada interna do protótipo. Não é autenticação de produção.
          </Typography>
        </Stack>
        <Stack component="form" onSubmit={submit} spacing={2}>
          <TextField
            error={Boolean(visibleError)}
            fullWidth
            id="development-profile"
            inputRef={profileInput}
            label="Perfil de desenvolvimento"
            onChange={(event) => {
              setProfileId(event.target.value);
              setValidationError(null);
            }}
            slotProps={{
              htmlInput: {
                "aria-describedby": visibleError ? profileErrorId : undefined,
              },
            }}
            value={profileId}
          />
          {visibleError ? (
            <Alert id={profileErrorId} role="alert" severity="error">
              {visibleError}
            </Alert>
          ) : null}
          <Button
            disabled={pending}
            fullWidth
            sx={(theme) => ({
              minHeight: theme.vinx.layout.controlMinHeight,
            })}
            type="submit"
            variant="contained"
          >
            {pending ? "Entrando…" : "Entrar"}
          </Button>
        </Stack>
      </Stack>
    </PublicLayout>
  );
}
