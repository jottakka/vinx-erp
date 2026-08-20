import { Link, List, ListItem, Stack, Typography } from "@mui/material";
import { Link as RouterLink } from "react-router-dom";
import type { AppNavigationLink } from "../shared/routing/app-paths";

type HelpPageProps = {
  navigationLinks: readonly AppNavigationLink[];
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
      <Stack component="section" spacing={1}>
        <Typography component="h2" variant="h6">
          Navegação
        </Typography>
        <Typography color="text.secondary">
          Em telas maiores, use a navegação lateral. Em telas menores, abra o
          menu no cabeçalho. Cada item leva a uma página disponível do
          aplicativo.
        </Typography>
        <List>
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
      </Stack>
      <Stack component="section" spacing={1}>
        <Typography component="h2" variant="h6">
          Empresas autorizadas
        </Typography>
        <Typography color="text.secondary">
          A página inicial reúne as empresas que você pode consultar. A
          identidade visual de cada empresa ajuda na leitura, mas não altera
          suas permissões nem escolhe uma empresa para ações.
        </Typography>
      </Stack>
      <Stack component="section" spacing={1}>
        <Typography component="h2" variant="h6">
          Notificações
        </Typography>
        <Typography color="text.secondary">
          O ícone de notificações no cabeçalho mostra quantos alertas in-app
          você ainda não leu. Cada notificação identifica a empresa de origem;
          marcar como lida somente organiza a sua caixa e não altera nenhuma
          solicitação. Quando existir um próximo passo, o botão "Abrir origem"
          leva à tela correspondente.
        </Typography>
      </Stack>
      <Stack component="section" spacing={1}>
        <Typography component="h2" variant="h6">
          Aparência e densidade
        </Typography>
        <Typography color="text.secondary">
          Abra as preferências visuais no cabeçalho para escolher tema claro ou
          escuro, além da densidade confortável ou compacta. Essas escolhas
          mudam somente a apresentação da interface.
        </Typography>
      </Stack>
      <Stack component="section" spacing={1}>
        <Typography component="h2" variant="h6">
          Sair
        </Typography>
        <Typography color="text.secondary">
          Use o botão Sair no cabeçalho quando terminar. Se ocorrer um problema,
          a aplicação permanece aberta e informa uma próxima tentativa segura.
        </Typography>
      </Stack>
    </Stack>
  );
}
