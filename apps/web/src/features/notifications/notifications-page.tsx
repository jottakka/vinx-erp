import MarkEmailReadOutlinedIcon from "@mui/icons-material/MarkEmailReadOutlined";
import {
  Alert,
  Box,
  Button,
  Card,
  CardContent,
  Chip,
  CircularProgress,
  Stack,
  Typography,
} from "@mui/material";
import { useCallback, useEffect, useMemo, useState } from "react";
import { Link as RouterLink } from "react-router-dom";
import { PageHeader } from "../../shared/ui/page-header";
import { useSession } from "../auth/session-provider";
import {
  createNotificationsClient,
  type NotificationInboxItem,
  type NotificationsClient,
  type NotificationsError,
} from "./notifications-client";
import { useNotifications } from "./notifications-provider";

const client = createNotificationsClient();
const pageSize = 20;

const allowlistedHrefs = new Set(["/paguai", "/atendimento"]);

function message(error: NotificationsError) {
  return (
    {
      400: "Requisição inválida. Atualize a página e tente novamente.",
      401: "Sua sessão expirou.",
      404: "Notificação não encontrada ou fora do seu acesso.",
      429: "Muitas tentativas. Aguarde antes de tentar novamente.",
      503: "Notificações estão indisponíveis. Tente novamente.",
      network: "Não foi possível alcançar o servidor. Tente novamente.",
      protocol: "A resposta do servidor não pôde ser validada.",
    } as const
  )[error];
}

function companyNameFor(
  companies: readonly { companyId: string; companyName: string }[],
  companyId: string,
) {
  return companies.find((company) => company.companyId === companyId)
    ?.companyName;
}

function relativeDate(iso: string) {
  const date = new Date(iso);
  const diffMs = Date.now() - date.getTime();
  const minutes = Math.round(diffMs / 60000);
  if (minutes < 1) return "agora";
  if (minutes < 60) return `há ${minutes} min`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `há ${hours} h`;
  return date.toLocaleDateString("pt-BR");
}

export type NotificationsPageProps = {
  notificationsClient?: NotificationsClient;
};

export function NotificationsPage({
  notificationsClient = client,
}: NotificationsPageProps) {
  const { bootstrap } = useSession();
  const { refreshUnreadCount } = useNotifications();
  const companies = useMemo(() => bootstrap?.companies ?? [], [bootstrap]);
  const [items, setItems] = useState<readonly NotificationInboxItem[]>([]);
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const [status, setStatus] = useState<"idle" | "loading" | "loading-more">(
    "loading",
  );
  const [error, setError] = useState<string | null>(null);
  const [markingId, setMarkingId] = useState<string | null>(null);
  const [markErrorId, setMarkErrorId] = useState<string | null>(null);

  const loadFirstPage = useCallback(async () => {
    setStatus("loading");
    setError(null);
    const result = await notificationsClient.fetchInboxPage(null, pageSize);
    if (result.ok) {
      setItems(result.value.items);
      setNextCursor(result.value.nextCursor);
      setStatus("idle");
    } else {
      setError(message(result.error));
      setStatus("idle");
    }
  }, [notificationsClient]);

  useEffect(() => {
    void loadFirstPage();
    void refreshUnreadCount();
  }, [loadFirstPage, refreshUnreadCount]);

  async function loadMore() {
    if (!nextCursor) return;
    setStatus("loading-more");
    const result = await notificationsClient.fetchInboxPage(
      nextCursor,
      pageSize,
    );
    if (result.ok) {
      setItems((previous) => {
        // Dedupe contra os já exibidos e dentro do próprio lote recebido.
        const seen = new Set(previous.map((item) => item.id));
        const incoming: NotificationInboxItem[] = [];
        for (const item of result.value.items) {
          if (seen.has(item.id)) continue;
          seen.add(item.id);
          incoming.push(item);
        }
        return [...previous, ...incoming];
      });
      setNextCursor(result.value.nextCursor);
    } else {
      setError(message(result.error));
    }
    setStatus("idle");
  }

  async function markAsRead(item: NotificationInboxItem) {
    setMarkingId(item.id);
    setMarkErrorId(null);
    const result = await notificationsClient.markNotificationRead(item.id);
    setMarkingId(null);
    if (result.ok) {
      // Confirmação pelo servidor antes de mudar o estado visual.
      setItems((previous) =>
        previous.map((current) =>
          current.id === item.id
            ? { ...current, readAt: new Date().toISOString() }
            : current,
        ),
      );
      await refreshUnreadCount();
    } else {
      setMarkErrorId(item.id);
    }
  }

  return (
    <Box
      component="section"
      aria-label="Notificações"
      sx={{ maxWidth: "44rem" }}
    >
      <PageHeader
        description="Alertas in-app das empresas que você acessa. Marcar como lida não altera nenhuma solicitação — o próximo passo continua na tela de origem."
        eyebrow="Caixa de entrada"
        title="Notificações"
      />
      {error ? (
        <Alert
          role="alert"
          severity="error"
          sx={{ mb: 2 }}
          action={
            <Button
              color="inherit"
              onClick={() => void loadFirstPage()}
              size="small"
            >
              Tentar novamente
            </Button>
          }
        >
          {error}
        </Alert>
      ) : null}
      {status === "loading" ? (
        <Stack
          aria-label="Carregando notificações"
          role="status"
          spacing={2}
          sx={{ py: 4 }}
        >
          <CircularProgress aria-hidden sx={{ mx: "auto" }} />
        </Stack>
      ) : !error && items.length === 0 ? (
        <Alert severity="info" sx={{ mt: 2 }}>
          Você não tem notificações no momento.
        </Alert>
      ) : (
        <Stack spacing={2} sx={{ mt: 2 }}>
          {items.map((item) => {
            const companyName = companyNameFor(companies, item.companyId);
            const actionHref =
              item.action && allowlistedHrefs.has(item.action.href)
                ? item.action.href
                : null;
            // Nome acessível único mesmo com várias notificações da mesma
            // empresa: empresa + resumo + sufixo curto do id.
            const summary = item.event ?? "atualização";
            const itemLabel = `${companyName ?? "Empresa"}: ${summary} (${item.id.slice(0, 8)})`;
            return (
              <Card
                key={item.id}
                variant="outlined"
                sx={{
                  borderColor: item.readAt ? "divider" : "primary.main",
                  borderWidth: item.readAt ? 1 : 2,
                }}
              >
                <CardContent>
                  <Stack
                    direction={{ xs: "column", sm: "row" }}
                    spacing={1}
                    sx={{ justifyContent: "space-between" }}
                  >
                    <Stack spacing={0.5} sx={{ minWidth: 0 }}>
                      <Stack
                        direction="row"
                        spacing={1}
                        sx={{
                          alignItems: "center",
                          flexWrap: "wrap",
                          rowGap: 0.5,
                        }}
                      >
                        <Chip
                          aria-label={`Empresa de origem: ${companyName ?? "Empresa"}`}
                          label={companyName ?? "Empresa"}
                          size="small"
                        />
                        {!item.readAt ? (
                          <Chip
                            aria-label="Não lida"
                            color="primary"
                            label="Não lida"
                            size="small"
                            variant="outlined"
                          />
                        ) : null}
                      </Stack>
                      <Typography variant="body1">
                        {item.event ?? `${item.source}: atualização`}
                      </Typography>
                      <Typography variant="caption" color="text.secondary">
                        {relativeDate(item.createdAt)}
                      </Typography>
                    </Stack>
                    <Stack
                      direction={{ xs: "column", sm: "row" }}
                      spacing={1}
                      sx={{ alignItems: { xs: "stretch", sm: "center" } }}
                    >
                      {actionHref ? (
                        <Button
                          aria-label={`Abrir origem: ${itemLabel}`}
                          component={RouterLink}
                          size="small"
                          to={actionHref}
                          variant="outlined"
                        >
                          Abrir origem
                        </Button>
                      ) : null}
                      {!item.readAt ? (
                        <Button
                          aria-label={`Marcar como lida: ${itemLabel}`}
                          disabled={markingId === item.id}
                          onClick={() => void markAsRead(item)}
                          size="small"
                          startIcon={
                            markingId === item.id ? undefined : (
                              <MarkEmailReadOutlinedIcon />
                            )
                          }
                        >
                          {markingId === item.id ? (
                            <CircularProgress aria-hidden size={18} />
                          ) : (
                            "Marcar como lida"
                          )}
                        </Button>
                      ) : null}
                    </Stack>
                  </Stack>
                  {markErrorId === item.id ? (
                    <Alert
                      role="alert"
                      severity="warning"
                      sx={{ mt: 1 }}
                      action={
                        <Button
                          aria-label={`Repetir marcação: ${itemLabel}`}
                          color="inherit"
                          onClick={() => void markAsRead(item)}
                          size="small"
                        >
                          Repetir
                        </Button>
                      }
                    >
                      Não foi possível marcar como lida. A notificação continua
                      não lida.
                    </Alert>
                  ) : null}
                </CardContent>
              </Card>
            );
          })}
          {nextCursor ? (
            <Button
              disabled={status === "loading-more"}
              onClick={() => void loadMore()}
              sx={{ alignSelf: "center" }}
            >
              {status === "loading-more" ? "Carregando…" : "Carregar mais"}
            </Button>
          ) : null}
        </Stack>
      )}
    </Box>
  );
}
