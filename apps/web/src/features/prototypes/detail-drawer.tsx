import CloseOutlinedIcon from "@mui/icons-material/CloseOutlined";
import { Box, Drawer, IconButton, Stack, Typography } from "@mui/material";
import type { ReactNode } from "react";

type DetailDrawerProps = {
  actions?: ReactNode;
  children: ReactNode;
  onClose: () => void;
  open: boolean;
  title: string;
};

export function DetailDrawer({
  actions,
  children,
  onClose,
  open,
  title,
}: DetailDrawerProps) {
  return (
    <Drawer anchor="right" onClose={onClose} open={open}>
      <Box sx={{ maxWidth: "100vw", p: 3, width: { sm: 480, xs: "100vw" } }}>
        <Stack direction="row" spacing={2} sx={{ alignItems: "center" }}>
          <Typography component="h2" sx={{ flex: 1 }} variant="h6">
            {title}
          </Typography>
          <IconButton aria-label="Fechar detalhes" onClick={onClose}>
            <CloseOutlinedIcon />
          </IconButton>
        </Stack>
        <Box sx={{ py: 3 }}>{children}</Box>
        {actions ? (
          <Stack direction="row" spacing={1}>
            {actions}
          </Stack>
        ) : null}
      </Box>
    </Drawer>
  );
}
