import { Box, MenuItem, TextField, Typography } from "@mui/material";
import { companySurface } from "../../app/theme/surface-tokens";
import type { AuthorizedCompanyMembership } from "../../features/auth/session-contract";
import { resolveCompanyAppearance } from "./company-appearance";

type CompanySelectProps = {
  companies: readonly Pick<
    AuthorizedCompanyMembership,
    "companyId" | "companyName"
  >[];
  disabled?: boolean;
  id?: string;
  label?: string;
  onChange: (companyId: string) => void;
  value: string;
};

function CompanyMonogram({ id, name }: { id: string; name: string }) {
  const { monogram, slot } = resolveCompanyAppearance(id, name);
  const accent = companySurface(slot);
  return (
    <Box
      aria-hidden="true"
      data-monogram={monogram}
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
        fontWeight: 700,
        height: 26,
        justifyContent: "center",
        width: 26,
      }}
    >
      {monogram}
    </Box>
  );
}

/**
 * Seletor de empresa com monograma colorido, usado nas páginas com escopo por
 * empresa (Paguai, Atendimento/Kanban, Pessoas).
 */
export function CompanySelect({
  companies,
  disabled,
  id,
  label = "Empresa",
  onChange,
  value,
}: CompanySelectProps) {
  return (
    <TextField
      disabled={disabled}
      id={id}
      label={label}
      onChange={(event) => onChange(event.target.value)}
      select
      value={value}
    >
      {companies.length === 0 ? (
        <MenuItem disabled value="">
          Sem empresa disponível
        </MenuItem>
      ) : null}
      {companies.map((company) => {
        const { monogram } = resolveCompanyAppearance(
          company.companyId,
          company.companyName,
        );
        return (
          <MenuItem key={company.companyId} value={company.companyId}>
            <Box
              component="span"
              sx={{ alignItems: "center", display: "inline-flex", gap: 1 }}
            >
              <CompanyMonogram
                id={company.companyId}
                name={company.companyName}
              />
              <Typography component="span">{company.companyName}</Typography>
              <Typography
                aria-hidden="true"
                color="text.secondary"
                component="span"
                sx={{ fontSize: "0.7rem" }}
                variant="caption"
              >
                {monogram}
              </Typography>
            </Box>
          </MenuItem>
        );
      })}
    </TextField>
  );
}
