import { Autocomplete, Avatar, Box, Chip, TextField } from "@mui/material";
import { useMemo } from "react";
import type { PersonOption } from "./atendimento-client";
import { initialsFor } from "./atendimento-lookup";
import { useDirectoryOptions } from "./use-directory-options";

/**
 * Picker de pessoas por empresa (nome + avatar de iniciais); guarda apenas o
 * membership-id internamente. Controle único reutilizado para responsável,
 * solicitante e observadores.
 */
export function PeoplePicker({
  companyId,
  label,
  helperText,
  value,
  onChange,
  disabled,
}: {
  companyId: string;
  disabled?: boolean;
  helperText?: string;
  label: string;
  onChange: (membershipId: string | null) => void;
  value: string | null;
}) {
  const { failed, options } = useDirectoryOptions(companyId);
  const loading = !failed && companyId.length > 0 && options.length === 0;

  const selected = useMemo(
    () =>
      options.find((option) => option.membershipId === value) ??
      (value && options.length === 0
        ? ({
            displayName: value,
            employeeId: value,
            membershipId: value,
          } as PersonOption)
        : null),
    [options, value],
  );

  return (
    <Autocomplete<PersonOption, false, false, false>
      disablePortal
      disabled={disabled}
      getOptionLabel={(option) => option.displayName}
      isOptionEqualToValue={(option, candidate) =>
        option.membershipId === candidate.membershipId
      }
      loading={loading}
      noOptionsText={
        failed
          ? "Não foi possível carregar as pessoas."
          : "Nenhuma pessoa encontrada."
      }
      onChange={(_, selectedOption) =>
        onChange(selectedOption?.membershipId ?? null)
      }
      onBlur={() => undefined}
      options={options}
      renderInput={(params) => (
        <TextField
          {...params}
          error={failed}
          helperText={
            failed
              ? "Sem acesso ao diretório desta empresa ou falha temporária."
              : helperText
          }
          label={label}
        />
      )}
      renderOption={(props, option) => {
        return (
          <Box component="li" {...props} key={option.membershipId}>
            <Avatar sx={{ fontSize: 12, height: 28, mr: 1.25, width: 28 }}>
              {initialsFor(option.displayName)}
            </Avatar>
            {option.displayName}
            {option.employeeId === option.membershipId ? (
              <Chip
                color="warning"
                label="ID manual"
                size="small"
                sx={{ ml: "auto" }}
              />
            ) : null}
          </Box>
        );
      }}
      size="small"
      sx={{ minWidth: 220 }}
      value={selected}
    />
  );
}
