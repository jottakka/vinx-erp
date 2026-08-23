import { Box, Stack, Typography } from "@mui/material";
import type { ReactNode } from "react";

type PageHeaderProps = {
  actions?: ReactNode;
  description: string;
  eyebrow?: string;
  title: string;
};

export function PageHeader({
  actions,
  description,
  eyebrow,
  title,
}: PageHeaderProps) {
  return (
    <Stack component="header" spacing={1}>
      {eyebrow ? (
        <Stack direction="row" spacing={1.25} sx={{ alignItems: "center" }}>
          <Box
            aria-hidden="true"
            sx={{
              bgcolor: "primary.main",
              borderRadius: 999,
              height: 2,
              width: 26,
            }}
          />
          <Typography
            color="primary"
            component="p"
            sx={{ lineHeight: 1 }}
            variant="overline"
          >
            {eyebrow}
          </Typography>
        </Stack>
      ) : null}
      <Stack
        direction={{ xs: "column", sm: "row" }}
        sx={{ alignItems: { sm: "center" }, gap: 2 }}
      >
        <Box sx={{ flex: 1 }}>
          <Typography component="h1" variant="h4">
            {title}
          </Typography>
          <Typography
            color="text.secondary"
            sx={{ mt: 0.5, maxWidth: "46rem" }}
          >
            {description}
          </Typography>
        </Box>
        {actions}
      </Stack>
    </Stack>
  );
}
