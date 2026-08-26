import { Box } from "@mui/material";
import { useColorScheme, useTheme } from "@mui/material/styles";
import type { ReactNode } from "react";
import type { CompanyAppearanceSlot } from "../../app/theme/company-appearance-slot";

type CompanyAccentScopeProps = {
  children: ReactNode;
  slot: CompanyAppearanceSlot;
};

/**
 * Colore uma árvore de conteúdo com o acento da empresa ativa: sobrescreve as
 * variáveis CSS do tema usadas por primary (botões, navegação ativa, lavagem
 * ambiente) com as do slot de aparência da empresa. Os valores são lidos do
 * tema em JS e aplicados por CSS variables, então a troca claro/escuro continua
 * refletindo o esquema ativo sem recomposição de estado.
 */
export function CompanyAccentScope({
  children,
  slot,
}: CompanyAccentScopeProps) {
  const theme = useTheme();
  const { mode = "light" } = useColorScheme();
  const accent = theme.palette.companyAccent[slot];

  const scopeVars = {
    "--vinx-palette-primary-light": accent.light,
    "--vinx-palette-primary-main": accent.main,
    "--vinx-palette-primary-dark": accent.dark,
    "--vinx-palette-primary-contrastText": accent.contrastText,
  } as const;

  return (
    <Box key={mode} sx={{ ...scopeVars, colorScheme: mode } as never}>
      {children}
    </Box>
  );
}
