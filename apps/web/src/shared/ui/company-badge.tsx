import { Box, Paper, Typography } from "@mui/material";
import { alpha, useTheme } from "@mui/material/styles";
import { vinxShadow } from "../../app/theme/surface-tokens";
import { resolveCompanyAppearance } from "./company-appearance";

type CompanyBadgeProps = {
  id: string;
  name: string;
};

export function CompanyBadge({ id, name }: CompanyBadgeProps) {
  const { monogram, slot } = resolveCompanyAppearance(id, name);
  const theme = useTheme();
  const accent = theme.palette.companyAccent?.[slot] ?? theme.palette.primary;

  return (
    <Paper
      aria-label={`Empresa ${name}`}
      data-monogram={monogram}
      role="group"
      sx={{
        alignItems: "center",
        bgcolor: (paperTheme) =>
          alpha(paperTheme.palette.background.paper, 0.6),
        borderColor: (_paperTheme) => alpha(accent.main, 0.5),
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
          <rect fill="currentColor" height="32" rx="10" width="32" />
          <text
            dominantBaseline="central"
            fill={accent.contrastText}
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
