import { Box, Typography } from "@mui/material";

export function AppLogo({ compact = false }: { compact?: boolean }) {
  return (
    <Box
      aria-label="VINX ERP"
      component="span"
      role="img"
      sx={{ alignItems: "center", display: "flex", gap: 1 }}
    >
      <Box
        aria-hidden="true"
        sx={{
          alignItems: "center",
          bgcolor: "primary.main",
          borderRadius: 1.5,
          color: "primary.contrastText",
          display: "flex",
          fontWeight: 800,
          height: compact ? 32 : 40,
          justifyContent: "center",
          width: compact ? 32 : 40,
        }}
      >
        V
      </Box>
      <Typography
        component="span"
        sx={{ fontWeight: 800, letterSpacing: "0.08em" }}
        variant={compact ? "subtitle1" : "h6"}
      >
        {compact ? "VINX" : "VINX ERP"}
      </Typography>
    </Box>
  );
}
