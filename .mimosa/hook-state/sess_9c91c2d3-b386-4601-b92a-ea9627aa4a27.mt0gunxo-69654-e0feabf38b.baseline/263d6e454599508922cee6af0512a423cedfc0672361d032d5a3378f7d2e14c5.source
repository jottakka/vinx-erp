import { Box, Typography } from "@mui/material";
import { useId } from "react";

export function AppLogo({ compact = false }: { compact?: boolean }) {
  const gradientId = useId();

  return (
    <Box
      aria-label="VINX ERP"
      component="span"
      role="img"
      sx={{ alignItems: "center", display: "inline-flex", gap: 1.25 }}
    >
      <Box
        aria-hidden="true"
        component="svg"
        sx={{
          borderRadius: "26%",
          display: "block",
          height: compact ? 30 : 38,
          width: compact ? 30 : 38,
        }}
        viewBox="0 0 40 40"
      >
        <defs>
          <linearGradient id={gradientId} x1="0" x2="1" y1="0" y2="1">
            <stop offset="0%" stopColor="var(--vinx-palette-primary-light)" />
            <stop offset="100%" stopColor="var(--vinx-palette-primary-dark)" />
          </linearGradient>
        </defs>
        <rect
          height="38"
          rx="11"
          style={{ fill: `url(#${gradientId})` }}
          width="38"
          x="1"
          y="1"
        />
        <path
          d="M11.5 13.5 L20 28.5 L28.5 13.5"
          fill="none"
          stroke="var(--vinx-palette-primary-contrastText)"
          strokeLinecap="round"
          strokeLinejoin="round"
          strokeWidth="3.4"
        />
      </Box>
      <Box
        component="span"
        sx={{ alignItems: "baseline", display: "flex", gap: 0.75 }}
      >
        <Typography
          component="span"
          sx={{ fontWeight: 800, letterSpacing: "0.14em" }}
          variant={compact ? "subtitle1" : "h6"}
        >
          VINX
        </Typography>
        {compact ? null : (
          <Typography
            color="text.secondary"
            component="span"
            sx={{ fontWeight: 600, letterSpacing: "0.3em" }}
            variant="caption"
          >
            ERP
          </Typography>
        )}
      </Box>
    </Box>
  );
}
