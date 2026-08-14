import { Card, CardContent, Stack, Typography } from "@mui/material";
import { Link } from "react-router-dom";
import { PrototypeNotice } from "./prototype-notice";

export const prototypeCatalog = [
  {
    available: false,
    description: "Solicitações, conferência, financeiro e políticas",
    id: "paguai",
    label: "Paguai",
    path: "/prototipos/paguai",
  },
  {
    available: false,
    description: "Quadro, detalhe, movimento e histórico",
    id: "kanban",
    label: "Kanban",
    path: "/prototipos/kanban",
  },
  {
    available: false,
    description: "Fila, ticket, timeline e SLA",
    id: "atendimento",
    label: "Atendimento",
    path: "/prototipos/atendimento",
  },
  {
    available: false,
    description: "Diretório, ausências, RH e equipe",
    id: "pessoas",
    label: "Pessoas",
    path: "/prototipos/pessoas",
  },
] as const;

export function PrototypeIndexPage() {
  return (
    <Stack spacing={3}>
      <Stack spacing={1}>
        <Typography component="h1" variant="h4">
          Protótipos planejados
        </Typography>
        <Typography color="text.secondary">
          Espaço local para avaliar superfícies em desenvolvimento.
        </Typography>
      </Stack>
      <PrototypeNotice />
      <Stack spacing={2}>
        {prototypeCatalog.map((prototype) => (
          <Card component="section" key={prototype.id} variant="outlined">
            <CardContent>
              <Stack spacing={1}>
                <Typography component="h2" variant="h6">
                  {prototype.label}
                </Typography>
                <Typography color="text.secondary">
                  {prototype.description}
                </Typography>
                {prototype.available ? (
                  <Link to={prototype.path}>Abrir protótipo</Link>
                ) : (
                  <Typography>
                    {prototype.label}: em preparação nesta série
                  </Typography>
                )}
              </Stack>
            </CardContent>
          </Card>
        ))}
      </Stack>
    </Stack>
  );
}
