import {
  Alert,
  Box,
  Button,
  Card,
  CardContent,
  Stack,
  Typography,
} from "@mui/material";
import { Link as RouterLink } from "react-router-dom";
import { useSession } from "../features/auth/session-provider";
import { CompanyBadge } from "../shared/ui/company-badge";

const upcomingAreas = ["Paguai", "Atendimento", "Kanban", "Pessoas"] as const;

export function HomePage() {
  const { bootstrap } = useSession();

  if (!bootstrap) {
    return null;
  }

  return (
    <Stack spacing={4}>
      <Stack spacing={1}>
        <Typography component="h1" variant="h4">
          Início
        </Typography>
        <Typography variant="h6">Olá, {bootstrap.user.name}</Typography>
        <Typography color="text.secondary">
          Você está visualizando todas as empresas autorizadas.
        </Typography>
      </Stack>
      <Card component="section" variant="outlined">
        <CardContent>
          <Stack spacing={2}>
            <Typography component="h2" variant="h6">
              Comece por aqui
            </Typography>
            <Typography color="text.secondary">
              Consulte a ajuda para entender a navegação, suas empresas e as
              preferências visuais.
            </Typography>
            <Box>
              <Button component={RouterLink} to="/ajuda" variant="outlined">
                Consultar ajuda
              </Button>
            </Box>
          </Stack>
        </CardContent>
      </Card>
      <Stack component="section" spacing={2}>
        <Stack spacing={0.5}>
          <Typography component="h2" variant="h6">
            Empresas autorizadas
          </Typography>
          <Typography color="text.secondary">
            Todas as empresas autorizadas
          </Typography>
        </Stack>
        {bootstrap.companies.length ? (
          <Box
            sx={{
              display: "grid",
              gap: 2,
              gridTemplateColumns:
                "repeat(auto-fit, minmax(min(100%, 16rem), 1fr))",
            }}
          >
            {bootstrap.companies.map((company) => (
              <Card component="article" key={company.id} variant="outlined">
                <CardContent>
                  <Stack spacing={1.5}>
                    <CompanyBadge id={company.id} name={company.name} />
                    <Typography color="text.secondary">
                      Empresa disponível nesta visualização.
                    </Typography>
                  </Stack>
                </CardContent>
              </Card>
            ))}
          </Box>
        ) : (
          <Alert severity="info">
            <Stack spacing={1}>
              <Typography>
                Nenhuma empresa autorizada está disponível.
              </Typography>
              <Box>
                <Button component={RouterLink} to="/ajuda" variant="outlined">
                  Consultar orientações
                </Button>
              </Box>
            </Stack>
          </Alert>
        )}
      </Stack>
      <Stack component="section" spacing={2}>
        <Stack spacing={0.5}>
          <Typography component="h2" variant="h6">
            Estrutura em evolução
          </Typography>
          <Typography color="text.secondary">
            Estas áreas serão disponibilizadas progressivamente. Ainda não há
            ações ou dados operacionais nesta tela.
          </Typography>
        </Stack>
        <Box
          component="ul"
          sx={{
            display: "grid",
            gap: 2,
            gridTemplateColumns:
              "repeat(auto-fit, minmax(min(100%, 16rem), 1fr))",
            listStyle: "none",
            m: 0,
            p: 0,
          }}
        >
          {upcomingAreas.map((area) => (
            <Card component="li" key={area} variant="outlined">
              <CardContent>
                <Typography component="h3" variant="h6">
                  {area}
                </Typography>
                <Typography color="text.secondary">Em preparação</Typography>
              </CardContent>
            </Card>
          ))}
        </Box>
      </Stack>
    </Stack>
  );
}
