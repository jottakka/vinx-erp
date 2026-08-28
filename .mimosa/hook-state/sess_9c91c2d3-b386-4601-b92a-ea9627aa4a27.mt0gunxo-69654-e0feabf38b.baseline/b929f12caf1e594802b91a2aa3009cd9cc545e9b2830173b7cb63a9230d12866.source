import { Alert, Button, Stack, TextField, Typography } from "@mui/material";
import { type FormEvent, useRef, useState } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import { PublicLayout } from "../../app/public-layout";
import { resolveAppPath } from "../../shared/routing/app-paths";
import { useSession } from "./session-provider";

const loginErrorId = "login-error";

function returnPathFromState(state: unknown) {
  return state && typeof state === "object"
    ? resolveAppPath(Reflect.get(state, "returnPath"))
    : "/";
}

export function LoginPage() {
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [validationError, setValidationError] = useState<string | null>(null);
  const emailInput = useRef<HTMLInputElement>(null);
  const passwordInput = useRef<HTMLInputElement>(null);
  const { error, signIn, status } = useSession();
  const location = useLocation();
  const navigate = useNavigate();
  const visibleError = validationError ?? error;
  const returnPath = returnPathFromState(location.state);
  const pending = status === "pending";

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const normalizedEmail = email.trim();
    if (!normalizedEmail) {
      setValidationError("Informe seu e-mail");
      emailInput.current?.focus();
      return;
    }
    if (!password) {
      setValidationError("Informe sua senha");
      passwordInput.current?.focus();
      return;
    }

    setValidationError(null);
    try {
      if (await signIn({ email: normalizedEmail, password })) {
        navigate(returnPath, { replace: true });
        return;
      }
      passwordInput.current?.focus();
    } finally {
      setPassword("");
    }
  }

  return (
    <PublicLayout>
      <Stack sx={(theme) => ({ gap: `${theme.vinx.layout.contentGap}px` })}>
        <Stack spacing={1}>
          <Typography component="h1" variant="h4">
            Entrar
          </Typography>
          <Typography color="text.secondary" component="p">
            Informe suas credenciais para acessar o aplicativo.
          </Typography>
        </Stack>
        <Stack component="form" onSubmit={submit} spacing={2}>
          <TextField
            autoComplete="email"
            error={Boolean(visibleError)}
            fullWidth
            id="email"
            inputRef={emailInput}
            label="E-mail"
            onChange={(event) => {
              setEmail(event.target.value);
              setValidationError(null);
            }}
            slotProps={{
              htmlInput: {
                "aria-describedby": visibleError ? loginErrorId : undefined,
              },
            }}
            type="email"
            value={email}
          />
          <TextField
            autoComplete="current-password"
            error={Boolean(visibleError)}
            fullWidth
            id="password"
            inputRef={passwordInput}
            label="Senha"
            onChange={(event) => {
              setPassword(event.target.value);
              setValidationError(null);
            }}
            slotProps={{
              htmlInput: {
                "aria-describedby": visibleError ? loginErrorId : undefined,
              },
            }}
            type="password"
            value={password}
          />
          {visibleError ? (
            <Alert id={loginErrorId} role="alert" severity="error">
              {visibleError}
            </Alert>
          ) : null}
          <Button
            disabled={pending}
            fullWidth
            size="large"
            sx={(theme) => ({ minHeight: theme.vinx.layout.controlMinHeight })}
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
