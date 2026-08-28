import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { VinxThemeProvider } from "../../app/theme/vinx-theme-provider";
import { atendimentoClient, type PersonOption } from "./atendimento-client";
import { PeoplePicker } from "./people-picker";

const alfaCompanyId = "11111111-1111-4111-8111-111111111111";
const betaCompanyId = "22222222-2222-4222-8222-222222222222";
const employeeId = "77777777-7777-4777-8777-777777777777";
const alfaMembershipId = "88888888-8888-4888-8888-888888888888";
const betaMembershipId = "99999999-9999-4999-8999-999999999999";
const unknownMembershipId = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";

const alfaOptions: PersonOption[] = [
  { displayName: "Ana Alfa", employeeId, membershipId: alfaMembershipId },
];
const betaOptions: PersonOption[] = [
  { displayName: "Bruno Beta", employeeId, membershipId: betaMembershipId },
];

/** Troca a empresa do picker em tela para exercitar o vínculo por contexto. */
function Harness({ initialCompanyId }: { initialCompanyId: string }) {
  const [companyId, setCompanyId] = useState(initialCompanyId);
  return (
    <VinxThemeProvider>
      <PeoplePicker
        companyId={companyId}
        label="Pessoas"
        onChange={() => undefined}
        value={alfaMembershipId}
      />
      <button onClick={() => setCompanyId(betaCompanyId)} type="button">
        Trocar empresa
      </button>
    </VinxThemeProvider>
  );
}

afterEach(() => vi.restoreAllMocks());

describe("PeoplePicker", () => {
  it("clears previous company labels when the company changes", async () => {
    const user = userEvent.setup();
    vi.spyOn(atendimentoClient, "directoryOptions").mockImplementation(
      async (companyId) =>
        companyId === alfaCompanyId ? alfaOptions : betaOptions,
    );

    render(<Harness initialCompanyId={alfaCompanyId} />);

    const combobox = screen.getByRole("combobox", { name: "Pessoas" });
    await waitFor(() => expect(combobox).toHaveValue("Ana Alfa"));

    await user.click(screen.getByRole("button", { name: "Trocar empresa" }));

    // A empresa nova começa sem rótulos da anterior: o valor da alfa não é
    // resolvido pela lista da beta e cai no estado neutro.
    await waitFor(() => expect(combobox).toHaveValue(""));
    expect(screen.getByText("Não resolvido")).toBeVisible();
    expect(screen.queryByText("Ana Alfa")).not.toBeInTheDocument();

    await user.click(combobox);
    expect(
      await screen.findByRole("option", { name: /Bruno Beta/ }),
    ).toBeVisible();
    expect(screen.queryByRole("option", { name: /Ana Alfa/ })).toBeNull();
  });

  it("drops previous-company options in the same render as the switch", async () => {
    // A troca de empresa expõe lista vazia JÁ NA renderização da troca, sem
    // esperar fetch ou efeito — fail-closed síncrono do vínculo por empresa.
    const user = userEvent.setup();
    vi.spyOn(atendimentoClient, "directoryOptions").mockImplementation(
      async (companyId) =>
        new Promise((resolve) =>
          // Beta nunca resolve: só o estado síncrono importa aqui.
          setTimeout(() => resolve(companyId === alfaCompanyId ? alfaOptions : betaOptions), 10_000),
        ),
    );

    render(<Harness initialCompanyId={alfaCompanyId} />);
    const combobox = screen.getByRole("combobox", { name: "Pessoas" });
    await waitFor(() => expect(combobox).toHaveValue("Ana Alfa"));

    await user.click(screen.getByRole("button", { name: "Trocar empresa" }));

    // Mesma renderização da troca: sem waitFor, sem fetch resolvido.
    expect(combobox).toHaveValue("");
    expect(screen.getByText("Não resolvido")).toBeVisible();
    expect(screen.queryByText("Ana Alfa")).not.toBeInTheDocument();
  });

  it("shows a neutral unresolved state instead of the raw membership id", async () => {
    vi.spyOn(atendimentoClient, "directoryOptions").mockResolvedValue(
      alfaOptions,
    );

    render(
      <VinxThemeProvider>
        <PeoplePicker
          companyId={alfaCompanyId}
          label="Pessoas"
          onChange={() => undefined}
          value={unknownMembershipId}
        />
      </VinxThemeProvider>,
    );

    const combobox = screen.getByRole("combobox", { name: "Pessoas" });
    await waitFor(() => expect(combobox).toHaveValue(""));
    expect(screen.getByText("Não resolvido")).toBeVisible();
    expect(screen.queryByText(unknownMembershipId)).not.toBeInTheDocument();
  });
});
