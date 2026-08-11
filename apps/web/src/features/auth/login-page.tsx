import {
  Alert,
  Box,
  Button,
  Paper,
  Stack,
  TextField,
  Typography,
} from "@mui/material";
import { type FormEvent, useState } from "react";
import { useNavigate } from "react-router-dom";
import { AppLogo } from "../../shared/ui/app-logo";
import { AppearanceMenu } from "../../shared/ui/appearance-menu";
import { useSession } from "./session-provider";

export function LoginPage() {
  const [profileId, setProfileId] = useState("maria");
  const { error, signIn, status } = useSession();
  const navigate = useNavigate();

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();

    if (await signIn(profileId)) {
      navigate("/protected", { replace: true });
    }
  }

  return (
    <Box
      component="main"
      sx={{
        alignItems: "center",
        bgcolor: "background.default",
        color: "text.primary",
        display: "flex",
        justifyContent: "center",
        minHeight: "100dvh",
        p: { xs: 2, sm: 4 },
      }}
    >
      <Paper
        sx={{
          bgcolor: "background.paper",
          border: 1,
          borderColor: "divider",
          p: { xs: 2.5, sm: 4 },
          width: "min(100%, 32rem)",
        }}
        variant="outlined"
      >
        <Stack spacing={3}>
          <AppLogo compact />
          <Stack spacing={1}>
            <Typography component="h1" variant="h4">
              Entrar
            </Typography>
            <Typography color="text.secondary" component="p">
              Entrada interna do protótipo. Não é autenticação de produção.
            </Typography>
          </Stack>
          <AppearanceMenu />
          <Stack component="form" onSubmit={submit} spacing={2}>
            <TextField
              fullWidth
              id="development-profile"
              label="Perfil de desenvolvimento"
              onChange={(event) => setProfileId(event.target.value)}
              value={profileId}
            />
            <Button
              disabled={status === "pending"}
              type="submit"
              variant="contained"
            >
              Entrar
            </Button>
          </Stack>
          {error ? <Alert severity="error">{error}</Alert> : null}
        </Stack>
      </Paper>
    </Box>
  );
}
