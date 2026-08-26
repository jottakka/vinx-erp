import {
  Checkbox,
  FormControl,
  InputLabel,
  ListItemText,
  MenuItem,
  Select,
  type SelectChangeEvent,
} from "@mui/material";
import { useId } from "react";
import type { AuthorizedCompanyMembership } from "../../features/auth/session-contract";

type CompanyOption = Pick<
  AuthorizedCompanyMembership,
  "companyId" | "companyName"
>;

export type AuthorizedCompanyFilterProps = {
  companies: readonly CompanyOption[];
  disabled?: boolean;
  label: string;
  onChange: (selectedCompanyIds: string[]) => void;
  selectedCompanyIds: readonly string[];
};

const allCompaniesValue = "__all_authorized_companies__";
const allCompaniesLabel = "Todas as empresas autorizadas";

function selectedIdsFromEvent(event: SelectChangeEvent<string[]>) {
  const value = event.target.value;
  return Array.isArray(value) ? value : value.split(",");
}

export function AuthorizedCompanyFilter({
  companies,
  disabled,
  label,
  onChange,
  selectedCompanyIds,
}: AuthorizedCompanyFilterProps) {
  const labelId = `${useId()}-label`;
  const authorizedIds = new Set(companies.map((company) => company.companyId));
  const selected = selectedCompanyIds.filter((id) => authorizedIds.has(id));
  const selectedNames = new Map(
    companies.map((company) => [company.companyId, company.companyName]),
  );

  function handleChange(event: SelectChangeEvent<string[]>) {
    const next = selectedIdsFromEvent(event);
    if (next.includes(allCompaniesValue)) {
      onChange([]);
      return;
    }

    onChange(next.filter((id) => authorizedIds.has(id)));
  }

  return (
    <FormControl disabled={disabled} fullWidth size="small">
      <InputLabel id={labelId}>{label}</InputLabel>
      <Select
        displayEmpty
        label={label}
        labelId={labelId}
        multiple
        onChange={handleChange}
        renderValue={(value) => {
          const ids = value as string[];
          if (ids.length === 0) return allCompaniesLabel;
          return ids
            .map((id) => selectedNames.get(id))
            .filter((name): name is string => Boolean(name))
            .join(", ");
        }}
        value={selected}
      >
        <MenuItem
          onClick={() => onChange([])}
          selected={selected.length === 0}
          value={allCompaniesValue}
        >
          <Checkbox checked={selected.length === 0} tabIndex={-1} />
          <ListItemText primary={allCompaniesLabel} />
        </MenuItem>
        {companies.length === 0 ? (
          <MenuItem disabled value="__no_authorized_company__">
            Nenhuma empresa autorizada
          </MenuItem>
        ) : null}
        {companies.map((company) => (
          <MenuItem key={company.companyId} value={company.companyId}>
            <Checkbox
              checked={selected.includes(company.companyId)}
              tabIndex={-1}
            />
            <ListItemText primary={company.companyName} />
          </MenuItem>
        ))}
      </Select>
    </FormControl>
  );
}
