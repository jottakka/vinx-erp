import { Button, Card, CardContent, Stack, Typography } from "@mui/material";
import { useFeedback } from "../../shared/ui/feedback-provider";
import type { AgentProposal } from "./agent-contract";

const demoActionNotice = "Ação demonstrativa: nenhuma mutação foi executada.";

export function AgentConfirmationCard({
  proposal,
}: {
  proposal: AgentProposal;
}) {
  const { notify } = useFeedback();

  function announceDemoAction() {
    notify(demoActionNotice, "info");
  }

  return (
    <Card component="section" variant="outlined">
      <CardContent>
        <Stack spacing={1.5}>
          <Typography component="h3" variant="subtitle1">
            {proposal.title}
          </Typography>
          <Typography color="text.secondary">{proposal.summary}</Typography>
          <Typography>
            {proposal.amount.toLocaleString("pt-BR", {
              currency: "BRL",
              style: "currency",
            })}
          </Typography>
          <Stack direction={{ sm: "row", xs: "column" }} spacing={1}>
            <Button
              onClick={announceDemoAction}
              sx={{ alignSelf: "flex-start" }}
              variant="contained"
            >
              Aprovar execução
            </Button>
            <Button
              onClick={announceDemoAction}
              sx={{ alignSelf: "flex-start" }}
              variant="outlined"
            >
              Recusar
            </Button>
          </Stack>
          <Typography color="text.secondary" variant="caption">
            O agente nunca executa mutações nesta demonstração.
          </Typography>
        </Stack>
      </CardContent>
    </Card>
  );
}
