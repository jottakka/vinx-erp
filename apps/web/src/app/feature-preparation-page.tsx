import ArrowBackRoundedIcon from "@mui/icons-material/ArrowBackRounded";
import {
  Box,
  Button,
  Card,
  CardContent,
  Stack,
  Typography,
} from "@mui/material";
import { Link as RouterLink } from "react-router-dom";
import { PageHeader } from "../shared/ui/page-header";

type FeaturePreparationPageProps = {
  feature: string;
  scope: string;
};

export function FeaturePreparationPage({
  feature,
  scope,
}: FeaturePreparationPageProps) {
  return (
    <Stack spacing={3}>
      <PageHeader
        description={scope}
        eyebrow="Módulo em preparação"
        title={feature}
      />
      <Card variant="outlined">
        <CardContent>
          <Stack
            direction={{ xs: "column", sm: "row" }}
            spacing={3}
            sx={{ alignItems: { sm: "center" } }}
          >
            <Box
              aria-hidden="true"
              component="svg"
              sx={{
                color: "text.disabled",
                display: "block",
                flexShrink: 0,
                height: 96,
                width: 96,
              }}
              viewBox="0 0 96 96"
            >
              <rect
                fill="none"
                height="64"
                rx="14"
                stroke="currentColor"
                strokeDasharray="5 7"
                strokeWidth="2"
                width="64"
                x="16"
                y="16"
              />
              <rect
                fill="none"
                height="30"
                rx="8"
                stroke="var(--vinx-palette-primary-main)"
                strokeWidth="2"
                width="30"
                x="33"
                y="33"
              />
              <circle
                cx="48"
                cy="48"
                fill="var(--vinx-palette-primary-main)"
                r="4"
              />
              <path
                d="M48 16 v-6 M80 48 h6 M48 80 v6 M16 48 h-6"
                stroke="var(--vinx-palette-primary-main)"
                strokeLinecap="round"
                strokeWidth="2"
              />
            </Box>
            <Stack spacing={1} sx={{ minWidth: 0 }}>
              <Typography component="h2" variant="h6">
                Nada operacional por aqui ainda
              </Typography>
              <Typography color="text.secondary">
                Esta área ainda não possui dados ou ações operacionais.
              </Typography>
            </Stack>
          </Stack>
        </CardContent>
      </Card>
      <Box>
        <Button
          component={RouterLink}
          startIcon={<ArrowBackRoundedIcon />}
          to="/"
          variant="outlined"
        >
          Voltar ao Início
        </Button>
      </Box>
    </Stack>
  );
}
