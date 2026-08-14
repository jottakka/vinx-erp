import { Box, Paper, Stack, Typography } from "@mui/material";
import type { ReactNode } from "react";
import { AppLogo } from "../shared/ui/app-logo";
import { AppearanceMenu } from "../shared/ui/appearance-menu";

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
      <Box
        sx={(theme) => ({
          alignItems: "center",
          display: "grid",
          gap: `${theme.vinx.layout.contentGap}px`,
          gridTemplateColumns: {
            xs: "minmax(0, 1fr)",
            md: "minmax(0, 1fr) minmax(20rem, 28rem)",
          },
          maxWidth: "72rem",
          minWidth: 0,
          mx: "auto",
          width: "100%",
        })}
      >
        <Stack
          component="section"
          sx={(theme) => ({
            gap: `${theme.vinx.layout.contentGap}px`,
            minWidth: 0,
          })}
        >
          <AppLogo />
          <Typography
            color="text.secondary"
            component="p"
            sx={{ display: { xs: "none", md: "block" } }}
            variant="h5"
          >
            Ambiente interno de desenvolvimento
          </Typography>
        </Stack>

        <Paper
          sx={(theme) => ({
            bgcolor: "background.paper",
            borderColor: "divider",
            justifySelf: { xs: "stretch", md: "end" },
            maxWidth: "30rem",
            minWidth: 0,
            p: {
              xs: `${theme.vinx.layout.contentPadding / 2}px`,
              sm: `${theme.vinx.layout.contentPadding}px`,
            },
            width: "100%",
          })}
          variant="outlined"
        >
          <Stack sx={(theme) => ({ gap: `${theme.vinx.layout.contentGap}px` })}>
            <Box sx={{ display: "flex", justifyContent: "flex-end" }}>
              <AppearanceMenu />
            </Box>
            {children}
          </Stack>
        </Paper>
      </Box>
    </Box>
  );
}
