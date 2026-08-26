import { Box, Typography } from "@mui/material";
import { companySurface } from "../../app/theme/surface-tokens";
import { resolveCompanyAppearance } from "./company-appearance";

type CompanyIdentityProps =
  | {
      companyId: string;
      companyName: string;
    }
  | {
      id: string;
      name: string;
    };

function identityProps(props: CompanyIdentityProps) {
  if ("companyId" in props) {
    return { id: props.companyId, name: props.companyName };
  }

  return { id: props.id, name: props.name };
}

export function CompanyIdentity(props: CompanyIdentityProps) {
  const { id, name } = identityProps(props);
  const { monogram, slot } = resolveCompanyAppearance(id, name);
  const accent = companySurface(slot);

  // Wrapper neutro (span, sem role/aria-label): este componente aninha dentro
  // de Button/MenuItem, onde div+group seria HTML inválido e narraria o nome
  // duas vezes; o nome visível no Typography já carrega a informação.
  return (
    <Box
      component="span"
      data-company-slot={slot}
      sx={{ alignItems: "center", display: "inline-flex", gap: 1 }}
    >
      <Box
        aria-hidden="true"
        component="span"
        sx={{
          alignItems: "center",
          bgcolor: accent.tint,
          border: 1,
          borderColor: accent.border,
          borderRadius: 1.5,
          color: accent.main,
          display: "inline-flex",
          flexShrink: 0,
          fontSize: "0.75rem",
          fontWeight: 700,
          height: 28,
          justifyContent: "center",
          width: 28,
        }}
      >
        {monogram}
      </Box>
      <Typography component="span" sx={{ fontWeight: 600 }}>
        {name}
      </Typography>
    </Box>
  );
}
