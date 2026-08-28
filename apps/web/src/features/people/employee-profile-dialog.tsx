import { Stack, Typography } from "@mui/material";
import { CompanyIdentity } from "../../shared/ui/company-identity";
import { EntityDialog } from "../../shared/ui/entity-dialog";
import type { EmployeeDirectoryItem } from "./employee-directory-client";

type EmployeeProfileDialogProps = {
  companyName?: string;
  companyId?: string;
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
  companyId,
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
          {companyId && companyName ? (
            <CompanyIdentity companyId={companyId} companyName={companyName} />
          ) : null}
        </Stack>
      ) : null}
    </EntityDialog>
  );
}
