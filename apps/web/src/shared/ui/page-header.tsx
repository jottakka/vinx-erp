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
        <Typography color="primary" variant="overline">
          {eyebrow}
        </Typography>
      ) : null}
      <Stack
        direction={{ xs: "column", sm: "row" }}
        sx={{ alignItems: { sm: "center" }, gap: 2 }}
      >
        <Box sx={{ flex: 1 }}>
          <Typography component="h1" variant="h4">
            {title}
          </Typography>
          <Typography color="text.secondary">{description}</Typography>
        </Box>
        {actions}
      </Stack>
    </Stack>
  );
}
