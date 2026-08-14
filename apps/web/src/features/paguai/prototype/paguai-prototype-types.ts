export const paguaiCompanies = ["alfa", "beta"] as const;
export type PaguaiCompany = (typeof paguaiCompanies)[number];

export type PaguaiSection = "requests" | "conference" | "finance" | "policies";

export type PaguaiRequestStatus =
  | "draft"
  | "conference"
  | "approval"
  | "finance"
  | "paid"
  | "returned";

export type PaguaiRequest = {
  id: string;
  description: string;
  company: PaguaiCompany;
  status: PaguaiRequestStatus;
  amount: number;
  dueDate: string;
  invoiceState: "metadata" | "authorized";
  proofRequired: boolean;
};

export type PaguaiFilters = {
  company: PaguaiCompany | "all";
  status: PaguaiRequestStatus | "all";
};

export type PaguaiState = {
  requests: readonly PaguaiRequest[];
  selectedId: string | null;
  section: PaguaiSection;
  filters: PaguaiFilters;
  policyAudience: "Solicitantes" | "Gestores" | "Administradores";
  demoEvents: readonly string[];
};

export type PaguaiAction =
  | { type: "createDraft"; request: PaguaiRequest }
  | { type: "returnRequest"; id: string; reason: string }
  | { type: "confirmPayment"; id: string }
  | { type: "select"; id: string | null }
  | { type: "setSection"; section: PaguaiSection }
  | { type: "setCompanyFilter"; company: PaguaiFilters["company"] }
  | { type: "setStatusFilter"; status: PaguaiFilters["status"] }
  | { type: "setPolicyAudience"; audience: PaguaiState["policyAudience"] };
