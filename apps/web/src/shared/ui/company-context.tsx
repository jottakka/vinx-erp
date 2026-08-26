import { createContext, type ReactNode, useContext, useMemo } from "react";
import type { AuthorizedCompanyMembership } from "../../features/auth/session-contract";
import { CompanyAccentScope } from "./company-accent-scope";
import { resolveCompanyAppearance } from "./company-appearance";

type ActiveCompany = {
  companyId: string;
  companyName: string;
  membershipId: string;
  slot: ReturnType<typeof resolveCompanyAppearance>["slot"];
};

type CompanyContextValue = {
  activeCompany: ActiveCompany | null;
};

const CompanyContext = createContext<CompanyContextValue>({
  activeCompany: null,
});

type CompanyProviderProps = {
  children: ReactNode;
  company: Pick<
    AuthorizedCompanyMembership,
    "companyId" | "companyName" | "membershipId"
  > | null;
};

/**
 * Fornece a empresa ativa de uma página (a selecionada no seletor) e aplica o
 * acento de cor correspondente a todo o conteúdo, para que empresas diferentes
 * sejam visualmente distintas. A seleção em si é mantida pelo estado da página;
 * este provider apenas transmite contexto e colore a árvore.
 */
export function CompanyProvider({ children, company }: CompanyProviderProps) {
  const value = useMemo<CompanyContextValue>(() => {
    if (!company) return { activeCompany: null };
    const { slot } = resolveCompanyAppearance(
      company.companyId,
      company.companyName,
    );
    return {
      activeCompany: {
        companyId: company.companyId,
        companyName: company.companyName,
        membershipId: company.membershipId,
        slot,
      },
    };
  }, [company]);

  if (!company) {
    return (
      <CompanyContext.Provider value={value}>
        {children}
      </CompanyContext.Provider>
    );
  }

  return (
    <CompanyContext.Provider value={value}>
      <CompanyAccentScope slot={value.activeCompany?.slot ?? "neutral"}>
        {children}
      </CompanyAccentScope>
    </CompanyContext.Provider>
  );
}

export function useActiveCompany(): CompanyContextValue {
  return useContext(CompanyContext);
}
