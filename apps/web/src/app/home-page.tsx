import type { SvgIconComponent } from "@mui/icons-material";
import {
  Alert,
  Box,
  Button,
  Card,
  CardActionArea,
  CardContent,
  Stack,
  Typography,
} from "@mui/material";
import { Link as RouterLink } from "react-router-dom";
import { useSession } from "../features/auth/session-provider";
import type { AppNavigationLink } from "../shared/routing/app-paths";
import { CompanyBadge } from "../shared/ui/company-badge";
import { PageHeader } from "../shared/ui/page-header";

type HomePageProps = {
  navigationLinks: readonly (AppNavigationLink & {
    icon: SvgIconComponent;
  })[];
};

export function HomePage({ navigationLinks }: HomePageProps) {
  const { bootstrap } = useSession();
  const moduleLinks = navigationLinks.filter(
    (route) => route.navigation && route.group === "modules",
  );

  if (!bootstrap) {
    return null;
  }

  return (
    <Stack spacing={4}>
      <PageHeader
        description="Uma visão geral das empresas autorizadas e das áreas de trabalho do VINX."
        eyebrow={`Olá, ${bootstrap.user.name}`}
        title="Início"
      />
      <Card variant="outlined">
        <CardContent>
          <Stack spacing={1}>
            <Typography variant="h6">Contexto atual</Typography>
            <Typography color="text.secondary">
              Consulte empresas e abra áreas em preparação sem misturar
              contextos ou inventar dados operacionais.
            </Typography>
          </Stack>
        </CardContent>
      </Card>
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
            Estas áreas serão disponibilizadas progressivamente, com destinos
            honestos enquanto os módulos são preparados.
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
          {moduleLinks.map((route) => {
            const Icon = route.icon;

            return (
              <Card component="li" key={route.path} variant="outlined">
                <CardActionArea
                  component={RouterLink}
                  sx={{ height: "100%" }}
                  to={route.path}
                >
                  <CardContent>
                    <Stack direction="row" spacing={1.5}>
                      <Icon color="primary" />
                      <Stack spacing={0.5}>
                        <Typography component="h3" variant="h6">
                          {route.label}
                        </Typography>
                        <Typography color="text.secondary">
                          Módulo em preparação
                        </Typography>
                      </Stack>
                    </Stack>
                  </CardContent>
                </CardActionArea>
              </Card>
            );
          })}
        </Box>
      </Stack>
    </Stack>
  );
}
