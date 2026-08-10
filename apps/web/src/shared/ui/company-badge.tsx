import BusinessRounded from "@mui/icons-material/BusinessRounded";
import { Box, Paper, Typography, useTheme } from "@mui/material";
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
      sx={{
        alignItems: "center",
        borderColor: accent.main,
        borderLeftWidth: 4,
        display: "inline-flex",
        gap: 1,
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
        sx={{
          alignItems: "center",
          bgcolor: accent.main,
          borderRadius: "50%",
          color: accent.contrastText,
          display: "flex",
          height: 28,
          justifyContent: "center",
          width: 28,
        }}
      >
        <BusinessRounded fontSize="small" />
      </Box>
      <Typography component="span" sx={{ fontWeight: 700 }}>
        {name}
      </Typography>
    </Paper>
  );
}
