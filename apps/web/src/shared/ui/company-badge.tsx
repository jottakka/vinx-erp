import { Box, Paper, Typography } from "@mui/material";
import { companySurface, vinxShadow } from "../../app/theme/surface-tokens";
import { resolveCompanyAppearance } from "./company-appearance";

type CompanyBadgeProps = {
  id: string;
  name: string;
};

export function CompanyBadge({ id, name }: CompanyBadgeProps) {
  const { monogram, slot } = resolveCompanyAppearance(id, name);
  const accent = companySurface(slot);

  return (
    <Paper
      aria-label={`Empresa ${name}`}
      data-monogram={monogram}
      role="group"
      sx={{
        alignItems: "center",
        bgcolor: "background.paper",
        borderColor: accent.border,
        borderRadius: 2.5,
        boxShadow: vinxShadow.card,
        display: "inline-flex",
        gap: 1.25,
        px: 1.25,
        py: 0.75,
        width: "fit-content",
      }}
      variant="outlined"
    >
      <Box
        aria-hidden="true"
        component="span"
        data-monogram={monogram}
        sx={{ alignItems: "center", color: accent.main, display: "flex" }}
      >
        <svg
          aria-hidden="true"
          focusable="false"
          height={30}
          style={{ display: "block" }}
          viewBox="0 0 32 32"
          width={30}
        >
          <rect fill={accent.tint} height="32" rx="10" width="32" />
          <rect
            fill="none"
            height="31"
            rx="9.5"
            stroke="currentColor"
            strokeOpacity="0.4"
            strokeWidth="1"
            width="31"
            x="0.5"
            y="0.5"
          />
          <text
            dominantBaseline="central"
            fill={accent.main}
            fontSize="13"
            fontWeight="700"
            textAnchor="middle"
            x="16"
            y="17"
          >
            {monogram}
          </text>
        </svg>
      </Box>
      <Typography component="span" sx={{ fontWeight: 700 }}>
        {name}
      </Typography>
    </Paper>
  );
}
