import { Box, Card, Stack, Typography } from "@mui/material";
import { alpha } from "@mui/material/styles";
import type { ReactNode } from "react";
import { AppLogo } from "../shared/ui/app-logo";
import { AppearanceMenu } from "../shared/ui/appearance-menu";
import { InkBackground } from "./ink-background";

const heroHighlights = [
  "Protótipos navegáveis sem backend",
  "Dados de demonstração sempre identificados",
  "Preferências visuais persistentes",
] as const;

export function PublicLayout({ children }: { children: ReactNode }) {
  return (
    <Box
      component="main"
      sx={(theme) => ({
        bgcolor: "background.default",
        boxSizing: "border-box",
        color: "text.primary",
        display: "grid",
        minHeight: "100dvh",
        overflowX: "hidden",
        p: {
          xs: `${theme.vinx.layout.contentPadding / 2}px`,
          sm: `${theme.vinx.layout.contentPadding}px`,
        },
      })}
    >
      <InkBackground />
      <Box
        sx={(theme) => ({
          alignItems: "center",
          display: "grid",
          gap: `${theme.vinx.layout.contentGap}px`,
          gridTemplateColumns: {
            xs: "minmax(0, 1fr)",
            md: "minmax(0, 1fr) minmax(20rem, 27rem)",
          },
          maxWidth: "72rem",
          minWidth: 0,
          mx: "auto",
          position: "relative",
          width: "100%",
          zIndex: 1,
        })}
      >
        <Stack
          component="section"
          sx={(_theme) => ({
            gap: 2.5,
            minWidth: 0,
          })}
        >
          <AppLogo />
          <Box sx={{ display: { xs: "none", md: "block" } }}>
            <Typography
              color="primary"
              component="p"
              sx={{
                fontWeight: 700,
                letterSpacing: "0.24em",
                textTransform: "uppercase",
              }}
              variant="overline"
            >
              Ambiente interno
            </Typography>
            <Typography component="h2" sx={{ mt: 0.5 }} variant="h3">
              Desenvolvimento do VINX ERP
            </Typography>
            <Typography
              color="text.secondary"
              component="p"
              sx={{ mt: 1, maxWidth: "34rem" }}
            >
              Entre com o perfil autorizado para avaliar o shell, os protótipos
              e as preferências visuais antes da integração real.
            </Typography>
            <Stack
              component="ul"
              spacing={1.25}
              sx={{ listStyle: "none", m: 0, mt: 3, p: 0 }}
            >
              {heroHighlights.map((item) => (
                <Stack
                  component="li"
                  direction="row"
                  key={item}
                  spacing={1.5}
                  sx={{ alignItems: "center" }}
                >
                  <Box
                    aria-hidden="true"
                    sx={(theme) => ({
                      bgcolor: alpha(
                        theme.palette.primary.main,
                        theme.palette.mode === "dark" ? 0.16 : 0.1,
                      ),
                      border: 1,
                      borderColor: "divider",
                      borderRadius: "50%",
                      color: "primary.main",
                      display: "flex",
                      fontSize: 13,
                      height: 22,
                      justifyContent: "center",
                      width: 22,
                    })}
                  >
                    ✓
                  </Box>
                  <Typography color="text.secondary">{item}</Typography>
                </Stack>
              ))}
            </Stack>
          </Box>
        </Stack>

        <Card
          sx={(theme) => ({
            bgcolor: "background.paper",
            border: 1,
            borderColor: "divider",
            borderRadius: 3,
            boxShadow:
              theme.palette.mode === "dark" ? "none" : theme.shadows[4],
            justifySelf: { xs: "stretch", md: "end" },
            maxWidth: "30rem",
            minWidth: 0,
            p: {
              xs: `${theme.vinx.layout.contentPadding / 2}px`,
              sm: `${theme.vinx.layout.contentPadding}px`,
            },
            width: "100%",
          })}
        >
          <Stack sx={(theme) => ({ gap: `${theme.vinx.layout.contentGap}px` })}>
            <Box sx={{ display: "flex", justifyContent: "flex-end" }}>
              <AppearanceMenu />
            </Box>
            {children}
          </Stack>
        </Card>
      </Box>
    </Box>
  );
}
