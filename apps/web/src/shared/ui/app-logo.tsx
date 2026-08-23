import { Box, Typography } from "@mui/material";
import { useId } from "react";
import { displayFontFamily, vinxShadow } from "../../app/theme/surface-tokens";

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
          borderRadius: "28%",
          display: "block",
          filter: vinxShadow.markFilter,
          height: compact ? 30 : 38,
          width: compact ? 30 : 38,
        }}
        viewBox="0 0 40 40"
      >
        <defs>
          <linearGradient id={gradientId} x1="0" x2="1" y1="0" y2="1">
            <stop offset="0%" stopColor="var(--vinx-palette-primary-light)" />
            <stop offset="55%" stopColor="var(--vinx-palette-primary-main)" />
            <stop offset="100%" stopColor="var(--vinx-palette-primary-dark)" />
          </linearGradient>
        </defs>
        <rect
          height="38"
          rx="12"
          style={{ fill: `url(#${gradientId})` }}
          width="38"
          x="1"
          y="1"
        />
        <rect
          fill="none"
          height="32"
          rx="9"
          stroke="var(--vinx-palette-primary-contrastText)"
          strokeOpacity="0.24"
          strokeWidth="1"
          width="32"
          x="4"
          y="4"
        />
        <path
          d="M13 14.5 L20 26 L27 14.5"
          fill="none"
          stroke="var(--vinx-palette-primary-contrastText)"
          strokeLinecap="round"
          strokeLinejoin="round"
          strokeOpacity="0.35"
          strokeWidth="3"
          transform="translate(2.2 -2.2)"
        />
        <path
          d="M11.5 15.5 L20 28 L28.5 15.5"
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
          sx={{
            fontFamily: displayFontFamily,
            fontWeight: 700,
            letterSpacing: "0.16em",
          }}
          variant={compact ? "subtitle1" : "h6"}
        >
          VINX
        </Typography>
        {compact ? null : (
          <Typography
            color="text.secondary"
            component="span"
            sx={{ fontWeight: 600, letterSpacing: "0.34em" }}
            variant="caption"
          >
            ERP
          </Typography>
        )}
      </Box>
    </Box>
  );
}
