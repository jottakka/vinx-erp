import { Stack, Typography } from "@mui/material";
import { EntityDialog } from "../../shared/ui/entity-dialog";
import type { EmployeeDirectoryItem } from "./employee-directory-client";

type EmployeeProfileDialogProps = {
  companyName?: string;
  employee: EmployeeDirectoryItem | null;
  onClose: () => void;
  open: boolean;
};

/**
 * Perfil de funcionário (somente leitura). Exibe os campos disponíveis no
 * diretório; o e-mail não é retornado pela listagem nesta versão.
 */
export function EmployeeProfileDialog({
  companyName,
  employee,
  onClose,
  open,
}: EmployeeProfileDialogProps) {
  return (
    <EntityDialog
      description={
        companyName ? `Funcionário de ${companyName}.` : "Funcionário."
      }
      onClose={onClose}
      open={open}
      title="Visualizar funcionário"
    >
      {employee ? (
        <Stack spacing={1.5}>
          <Typography component="p" variant="subtitle1">
            {employee.displayName}
          </Typography>
          <Stack spacing={1}>
            <Typography color="text.secondary" variant="body2">
              Identificador do funcionário
            </Typography>
            <Typography variant="body2">{employee.employeeId}</Typography>
          </Stack>
          <Stack spacing={1}>
            <Typography color="text.secondary" variant="body2">
              Membro da empresa
            </Typography>
            <Typography variant="body2">{employee.membershipId}</Typography>
          </Stack>
          <Typography color="text.secondary" variant="caption">
            O e-mail não é retornado pelo diretório nesta versão.
          </Typography>
        </Stack>
      ) : null}
    </EntityDialog>
  );
}
