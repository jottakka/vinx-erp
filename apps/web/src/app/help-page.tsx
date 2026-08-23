import {
  BusinessCenterOutlined,
  HelpOutlineRounded,
  LogoutRounded,
  NotificationsOutlined,
  PaletteOutlined,
} from "@mui/icons-material";
import { Box, Link, List, ListItem, Stack, Typography } from "@mui/material";
import type { ReactElement } from "react";
import { Link as RouterLink } from "react-router-dom";
import { vinxTint } from "../app/theme/surface-tokens";
import type { AppNavigationLink } from "../shared/routing/app-paths";

type HelpPageProps = {
  navigationLinks: readonly AppNavigationLink[];
};

type HelpSection = {
  content: ReactElement;
  icon: typeof HelpOutlineRounded;
  title: string;
};

export function HelpPage({ navigationLinks }: HelpPageProps) {
  return (
    <Stack spacing={4}>
      <Stack spacing={1}>
        <Typography component="h1" variant="h4">
          Ajuda
        </Typography>
        <Typography color="text.secondary">
          Use estas orientações para navegar com clareza no VINX ERP.
        </Typography>
      </Stack>
      <Box
        sx={{
          display: "grid",
          gap: 2,
          gridTemplateColumns:
            "repeat(auto-fit, minmax(min(100%, 20rem), 1fr))",
        }}
      >
        <HelpSectionCard
          content={
            <>
              <Typography color="text.secondary">
                Em telas maiores, use a navegação lateral. Em telas menores,
                abra o menu no cabeçalho. Cada item leva a uma página disponível
                do aplicativo.
              </Typography>
              <List dense sx={{ mt: 1 }}>
                {navigationLinks
                  .filter((item) => item.navigation)
                  .map((item) => (
                    <ListItem disableGutters key={item.path}>
                      <Link component={RouterLink} to={item.path}>
                        {item.label}
                      </Link>
                    </ListItem>
                  ))}
              </List>
            </>
          }
          icon={HelpOutlineRounded}
          title="Navegação"
        />
        <HelpSectionCard
          content={
            <Typography color="text.secondary">
              A página inicial reúne as empresas que você pode consultar. A
              identidade visual de cada empresa ajuda na leitura, mas não altera
              suas permissões nem escolhe uma empresa para ações.
            </Typography>
          }
          icon={BusinessCenterOutlined}
          title="Empresas autorizadas"
        />
        <HelpSectionCard
          content={
            <Typography color="text.secondary">
              O ícone de notificações no cabeçalho mostra quantos alertas in-app
              você ainda não leu. Cada notificação identifica a empresa de
              origem; marcar como lida somente organiza a sua caixa e não altera
              nenhuma solicitação. Quando existir um próximo passo, o botão
              "Abrir origem" leva à tela correspondente.
            </Typography>
          }
          icon={NotificationsOutlined}
          title="Notificações"
        />
        <HelpSectionCard
          content={
            <Typography color="text.secondary">
              Abra as preferências visuais no cabeçalho para escolher tema claro
              ou escuro, além da densidade confortável ou compacta. Essas
              escolhas mudam somente a apresentação da interface.
            </Typography>
          }
          icon={PaletteOutlined}
          title="Aparência e densidade"
        />
        <HelpSectionCard
          content={
            <Typography color="text.secondary">
              Use o botão Sair no cabeçalho quando terminar. Se ocorrer um
              problema, a aplicação permanece aberta e informa uma próxima
              tentativa segura.
            </Typography>
          }
          icon={LogoutRounded}
          title="Sair"
        />
      </Box>
    </Stack>
  );
}

function HelpSectionCard({ content, icon: Icon, title }: HelpSection) {
  return (
    <Stack component="section" spacing={1}>
      <Stack direction="row" spacing={1.5} sx={{ alignItems: "center" }}>
        <Box
          aria-hidden="true"
          sx={{
            alignItems: "center",
            bgcolor: vinxTint.primarySoft,
            borderRadius: 2.5,
            color: "primary.main",
            display: "flex",
            height: 38,
            justifyContent: "center",
            width: 38,
          }}
        >
          <Icon fontSize="small" />
        </Box>
        <Typography component="h2" variant="h6">
          {title}
        </Typography>
      </Stack>
      {content}
    </Stack>
  );
}
