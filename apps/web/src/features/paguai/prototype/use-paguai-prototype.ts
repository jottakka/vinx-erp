import { useMemo, useReducer } from "react";
import { paguaiInitialState } from "./paguai-prototype-fixtures";
import type {
  PaguaiAction,
  PaguaiCompany,
  PaguaiFilters,
  PaguaiRequest,
  PaguaiReturnReason,
  PaguaiSection,
  PaguaiState,
} from "./paguai-prototype-types";

export function paguaiReducer(
  state: PaguaiState,
  action: PaguaiAction,
): PaguaiState {
  switch (action.type) {
    case "createDraft":
      return {
        ...state,
        demoEvents: [
          ...state.demoEvents,
          {
            id: `draft-${action.request.id}`,
            message: "Alteração local de demonstração",
          },
        ],
        requests: [action.request, ...state.requests],
        selectedId: action.request.id,
      };
    case "returnRequest":
      return {
        ...state,
        demoEvents: [
          ...state.demoEvents,
          {
            id: `return-${action.id}`,
            message: "Solicitação devolvida para ajustes",
          },
        ],
        requests: state.requests.map((request) =>
          request.id === action.id
            ? {
                ...request,
                returnReason: action.reason,
                status: "returned",
              }
            : request,
        ),
      };
    case "confirmPayment":
      return {
        ...state,
        demoEvents: [
          ...state.demoEvents,
          {
            id: `payment-${action.id}`,
            message: "Pagamento confirmado localmente",
          },
        ],
        requests: state.requests.map((request) =>
          request.id === action.id ? { ...request, status: "paid" } : request,
        ),
      };
    case "select":
      return { ...state, selectedId: action.id };
    case "setSection":
      return { ...state, section: action.section };
    case "setCompanyFilter":
      return {
        ...state,
        filters: { ...state.filters, company: action.company },
      };
    case "setStatusFilter":
      return {
        ...state,
        filters: { ...state.filters, status: action.status },
      };
    case "setPolicyAudience":
      return { ...state, policyAudience: action.audience };
  }
}

function filterRequests(
  requests: readonly PaguaiRequest[],
  filters: PaguaiFilters,
) {
  return requests.filter(
    (request) =>
      (filters.company === "all" || request.company === filters.company) &&
      (filters.status === "all" || request.status === filters.status),
  );
}

export function usePaguaiPrototype() {
  const [state, dispatch] = useReducer(paguaiReducer, paguaiInitialState);
  const requests = useMemo(
    () => filterRequests(state.requests, state.filters),
    [state.filters, state.requests],
  );
  const selected = state.requests.find(
    (request) => request.id === state.selectedId,
  );
  const conferenceRequests = state.requests.filter(
    (request) => request.status === "conference",
  );
  const financeRequests = state.requests.filter(
    (request) => request.status === "finance",
  );

  return {
    actions: {
      confirmPayment: (id: string) => dispatch({ id, type: "confirmPayment" }),
      createDraft: (request: PaguaiRequest) =>
        dispatch({ request, type: "createDraft" }),
      returnRequest: (id: string, reason: PaguaiReturnReason) =>
        dispatch({ id, reason, type: "returnRequest" }),
      select: (id: string | null) => dispatch({ id, type: "select" }),
      setCompanyFilter: (company: PaguaiCompany | "all") =>
        dispatch({ company, type: "setCompanyFilter" }),
      setPolicyAudience: (audience: PaguaiState["policyAudience"]) =>
        dispatch({ audience, type: "setPolicyAudience" }),
      setSection: (section: PaguaiSection) =>
        dispatch({ section, type: "setSection" }),
      setStatusFilter: (status: PaguaiFilters["status"]) =>
        dispatch({ status, type: "setStatusFilter" }),
    },
    conferenceRequests,
    demoEvents: state.demoEvents,
    filters: state.filters,
    financeRequests,
    policyAudience: state.policyAudience,
    requests,
    selected,
    section: state.section,
  };
}
