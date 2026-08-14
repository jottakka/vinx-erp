import { Button, Card, CardContent, Stack, Typography } from "@mui/material";
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
          <Typography color="text.secondary">
            Esta área ainda não possui dados ou ações operacionais.
          </Typography>
        </CardContent>
      </Card>
      <Button component={RouterLink} to="/" variant="outlined">
        Voltar ao Início
      </Button>
    </Stack>
  );
}
