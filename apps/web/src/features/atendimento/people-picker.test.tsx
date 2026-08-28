import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { VinxThemeProvider } from "../../app/theme/vinx-theme-provider";
import { atendimentoClient, type PersonOption } from "./atendimento-client";
import { PeoplePicker } from "./people-picker";
import { useDirectoryOptions } from "./use-directory-options";

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
    // IDs inéditos (o cache de módulo das outras suítes não interfere), a
    // requisição da empresa destino fica pendurada para sempre e cada
    // renderização exposta pelo hook é registrada: a PRIMEIRA render da nova
    // empresa precisa já expor lista vazia — fail-closed síncrono.
    const freshAlfa = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaa01";
    const freshBeta = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaa02";
    vi.spyOn(atendimentoClient, "directoryOptions").mockImplementation(
      async (companyId) => {
        if (companyId === freshAlfa) return alfaOptions;
        return new Promise<PersonOption[]>(() => {});
      },
    );

    const renders: Array<{ companyId: string; options: PersonOption[] }> = [];
    function Probe({ companyId }: { companyId: string }) {
      const { options } = useDirectoryOptions(companyId);
      renders.push({ companyId, options });
      return (
        <ul>
          {options.map((option) => (
            <li key={option.membershipId}>{option.displayName}</li>
          ))}
        </ul>
      );
    }
    function SwitchHarness() {
      const [companyId, setCompanyId] = useState(freshAlfa);
      return (
        <>
          <Probe companyId={companyId} />
          <button onClick={() => setCompanyId(freshBeta)} type="button">
            Trocar empresa
          </button>
        </>
      );
    }

    render(<SwitchHarness />);
    await screen.findByText("Ana Alfa");

    await userEvent.click(
      screen.getByRole("button", { name: "Trocar empresa" }),
    );

    const firstBetaRender = renders.find(
      (entry) => entry.companyId === freshBeta,
    );
    expect(firstBetaRender).toBeDefined();
    expect(firstBetaRender?.options).toEqual([]);
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
