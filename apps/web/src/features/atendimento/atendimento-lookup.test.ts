import { describe, expect, it } from "vitest";
import {
  channelLabel,
  eventLabels,
  initialsFor,
  priorityColor,
  priorityLabel,
} from "./atendimento-lookup";
import {
  filterFromSearchParams,
} from "./atendimento-filter-bar";

describe("atendimento lookup single source", () => {
  it("maps priorities and channels to pt-BR labels from one table", () => {
    expect(priorityLabel("critica")).toBe("Crítica");
    expect(priorityLabel("desconhecida")).toBe("desconhecida");
    expect(priorityColor("critica")).toBe("error");
    expect(channelLabel("whatsapp")).toBe("WhatsApp");
    expect(eventLabels("watcher_added")).toBe("Observador adicionado");
  });

  it("derives avatar initials deterministically", () => {
    expect(initialsFor("ana clara souza")).toBe("AC");
    expect(initialsFor("Bea")).toBe("B");
  });
});

describe("shareable queue filters", () => {
  it("reads known filter params from the URL search string", () => {
    const filter = filterFromSearchParams(
      "?status=novo&priority=alta&overdue=true&search=fatura",
    );
    expect(filter).toMatchObject({
      overdue: true,
      priority: "alta",
      search: "fatura",
      status: "novo",
    });
  });

  it("ignores unknown values instead of leaking them to the API", () => {
    const filter = filterFromSearchParams("?priority=urgente&status=xpto");
    expect(filter.priority).toBeUndefined();
    expect(filter.status).toBeUndefined();
    expect(filter.overdue).toBe(false);
  });
});
