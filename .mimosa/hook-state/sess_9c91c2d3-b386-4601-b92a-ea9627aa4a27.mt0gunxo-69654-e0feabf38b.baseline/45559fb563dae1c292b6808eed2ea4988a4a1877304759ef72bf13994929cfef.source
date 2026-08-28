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
import { alpha } from "@mui/material/styles";
import { Link as RouterLink } from "react-router-dom";
import { useSession } from "../features/auth/session-provider";
import {
  type AppNavigationLink,
  paguaiPrototypePath,
} from "../shared/routing/app-paths";
import { CompanyBadge } from "../shared/ui/company-badge";
import { PageHeader } from "../shared/ui/page-header";

type HomePageProps = {
  navigationLinks: readonly (AppNavigationLink & {
    icon: SvgIconComponent;
  })[];
};

export function getAvailablePrototypePath(modulePath: string) {
  if (!import.meta.env.DEV || modulePath !== "/paguai") {
    return undefined;
  }

  return paguaiPrototypePath;
}

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
        eyebrow={`Olá, ${bootstrap.user.displayName}`}
        title="Início"
      />
      <Card
        component="section"
        sx={{
          borderColor: "divider",
          borderLeft: 3,
          borderLeftColor: "primary.main",
        }}
        variant="outlined"
      >
        <CardContent>
          <Stack spacing={2}>
            <Typography component="h2" variant="h6">
              Comece por aqui
            </Typography>
            <Typography color="text.secondary">
              Consulte a ajuda para entender a navegação, suas empresas e as
              preferências visuais. As áreas abaixo preservam o contexto
              explícito de cada empresa, sem misturar dados operacionais.
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
          <Typography
            color="primary"
            component="p"
            sx={{
              fontWeight: 700,
              letterSpacing: "0.2em",
              textTransform: "uppercase",
            }}
            variant="overline"
          >
            Contexto
          </Typography>
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
              <Card
                component="article"
                key={company.membershipId}
                variant="outlined"
              >
                <CardContent>
                  <Stack spacing={1.5}>
                    <CompanyBadge
                      id={company.companyId}
                      name={company.companyName}
                    />
                    <Typography color="text.secondary">
                      {company.capabilities.length
                        ? `Acesso autorizado a ${company.capabilities.length} ${
                            company.capabilities.length === 1
                              ? "capacidade"
                              : "capacidades"
                          } desta empresa.`
                        : "Nenhuma capacidade liberada para esta empresa."}
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
          <Typography
            color="primary"
            component="p"
            sx={{
              fontWeight: 700,
              letterSpacing: "0.2em",
              textTransform: "uppercase",
            }}
            variant="overline"
          >
            Módulos
          </Typography>
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
            const prototypePath = getAvailablePrototypePath(route.path);

            return (
              <Card
                component="li"
                key={route.path}
                sx={(theme) => ({
                  transition: theme.transitions.create(
                    ["transform", "box-shadow"],
                    { duration: theme.transitions.duration.shortest },
                  ),
                  "&:hover": {
                    boxShadow: theme.shadows[4],
                    transform: "translateY(-2px)",
                  },
                })}
                variant="outlined"
              >
                <CardActionArea
                  component={RouterLink}
                  sx={{ height: "100%" }}
                  to={route.path}
                >
                  <CardContent>
                    <Stack spacing={1.5}>
                      <Box
                        aria-hidden="true"
                        sx={(theme) => ({
                          alignItems: "center",
                          bgcolor: alpha(theme.palette.primary.main, 0.12),
                          borderRadius: 2,
                          color: "primary.main",
                          display: "flex",
                          height: 44,
                          justifyContent: "center",
                          width: 44,
                        })}
                      >
                        <Icon />
                      </Box>
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
                {prototypePath ? (
                  <Box sx={{ px: 2, pb: 2 }}>
                    <Button
                      component={RouterLink}
                      to={prototypePath}
                      variant="outlined"
                    >
                      Abrir protótipo {route.label}
                    </Button>
                  </Box>
                ) : null}
              </Card>
            );
          })}
        </Box>
      </Stack>
    </Stack>
  );
}
