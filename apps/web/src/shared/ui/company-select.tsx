import { MenuItem, TextField } from "@mui/material";
import type { AuthorizedCompanyMembership } from "../../features/auth/session-contract";
import { CompanyIdentity } from "./company-identity";

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
        return (
          <MenuItem key={company.companyId} value={company.companyId}>
            <CompanyIdentity
              companyId={company.companyId}
              companyName={company.companyName}
            />
          </MenuItem>
        );
      })}
    </TextField>
  );
}
