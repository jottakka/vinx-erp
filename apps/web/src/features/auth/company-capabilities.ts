import type { AuthorizedCompanyMembership } from "./session-contract";

export function hasCompanyCapability(
  company: Pick<AuthorizedCompanyMembership, "capabilities"> | undefined,
  capabilityName: string,
): boolean {
  return (
    company?.capabilities.some(
      (capability) =>
        capability.name === capabilityName &&
        capability.scopes.includes("Company"),
    ) ?? false
  );
}

/**
 * Permissões de escrita de uma entidade por empresa: a criação depende da
 * capacidade de create e a edição (mover/atribuir/alterar) depende de uma
 * capacidade de update/change. A leitura fica disponível sempre que a empresa
 * está autorizada na sessão; a autorização da operação continua sendo validada
 * pelo backend, esta projeção apenas orienta a UI.
 */
export type CompanyEntityPermissions = {
  canCreate: boolean;
  canEdit: boolean;
  canView: boolean;
};

export type CompanyEntityCapabilityNames = {
  create?: string;
  edit?: string;
};

export function resolveCompanyEntityPermissions(
  company: Pick<AuthorizedCompanyMembership, "capabilities"> | undefined,
  { create, edit }: CompanyEntityCapabilityNames,
): CompanyEntityPermissions {
  return {
    canCreate: create ? hasCompanyCapability(company, create) : false,
    canEdit: edit ? hasCompanyCapability(company, edit) : false,
    canView: Boolean(company),
  };
}
