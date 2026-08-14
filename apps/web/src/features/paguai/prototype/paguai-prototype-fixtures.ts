import type { PaguaiRequest, PaguaiState } from "./paguai-prototype-types";

export const paguaiRequestFixtures: readonly PaguaiRequest[] = [
  {
    amount: 1200,
    company: "alfa",
    description: "Renovação de licença",
    dueDate: "2026-08-30",
    id: "paguai-101",
    invoiceState: "metadata",
    proofRequired: false,
    status: "conference",
  },
  {
    amount: 950,
    company: "beta",
    description: "Serviço de manutenção",
    dueDate: "2026-08-28",
    id: "paguai-102",
    invoiceState: "authorized",
    proofRequired: true,
    status: "finance",
  },
  {
    amount: 480,
    company: "alfa",
    description: "Material de escritório",
    dueDate: "2026-09-05",
    id: "paguai-103",
    invoiceState: "metadata",
    proofRequired: false,
    status: "approval",
  },
];

export const paguaiInitialState: PaguaiState = {
  demoEvents: [],
  filters: { company: "all", status: "all" },
  policyAudience: "Solicitantes",
  requests: paguaiRequestFixtures,
  section: "requests",
  selectedId: null,
};
