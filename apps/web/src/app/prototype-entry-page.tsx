import { Box, Button, Paper, Stack, Typography } from "@mui/material";
import { useNavigate } from "react-router-dom";
import { useSession } from "../features/auth/session-provider";
import { AppLogo } from "../shared/ui/app-logo";
import { CompanyBadge } from "../shared/ui/company-badge";

export function PrototypeEntryPage() {
  const { bootstrap, signOut, status } = useSession();
  const navigate = useNavigate();

  if (!bootstrap) {
    return null;
  }

  async function leavePrototype() {
    await signOut();
    navigate("/login", { replace: true });
  }

  return (
    <Box
      component="main"
      sx={{
        bgcolor: "background.default",
        color: "text.primary",
        minHeight: "100dvh",
        p: { xs: 2, sm: 4 },
      }}
    >
      <Paper
        sx={{
          bgcolor: "background.paper",
          border: 1,
          borderColor: "divider",
          mx: "auto",
          p: { xs: 2.5, sm: 4 },
          width: "min(100%, 48rem)",
        }}
        variant="outlined"
      >
        <Stack spacing={3}>
          <AppLogo />
          <Stack spacing={0.5}>
            <Typography component="h1" variant="h4">
              Protótipo autenticado
            </Typography>
            <Typography color="text.secondary" component="p">
              {bootstrap.user.name}
            </Typography>
          </Stack>
          <Stack
            aria-label="Empresas autorizadas"
            component="ul"
            spacing={1.5}
            sx={{ listStyle: "none", m: 0, p: 0 }}
          >
            {bootstrap.companies.map((company) => (
              <Box component="li" key={company.id}>
                <Stack spacing={0.5}>
                  <CompanyBadge id={company.id} name={company.name} />
                  <Typography component="span" color="text.secondary">
                    {company.name}: {company.capabilities.join(", ")}
                  </Typography>
                </Stack>
              </Box>
            ))}
          </Stack>
          <Button
            disabled={status === "pending"}
            onClick={leavePrototype}
            type="button"
            variant="outlined"
          >
            Sair do protótipo
          </Button>
        </Stack>
      </Paper>
    </Box>
  );
}
