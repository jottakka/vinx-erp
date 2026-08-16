import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ComponentProps } from "react";
import { describe, expect, it, vi } from "vitest";
import type {
  FoundationBootstrapLoadResult,
  FoundationBootstrapSnapshot,
} from "./foundation-bootstrap-client";
import { createMockSessionGateway } from "./mock-session-gateway";
import { SessionProvider, useSession } from "./session-provider";

const alfa: FoundationBootstrapSnapshot["companies"][number] = {
  membershipId: "22222222-2222-4222-8222-222222222222",
  companyId: "33333333-3333-4333-8333-333333333333",
  companyName: "Alfa",
  capabilities: [{ name: "payment.approve", scopes: ["Company"] }],
};
const beta: FoundationBootstrapSnapshot["companies"][number] = {
  membershipId: "44444444-4444-4444-8444-444444444444",
  companyId: "55555555-5555-4555-8555-555555555555",
  companyName: "Beta",
  capabilities: [{ name: "people.view", scopes: ["Team"] }],
};

function snapshot(
  companies: FoundationBootstrapSnapshot["companies"],
): FoundationBootstrapSnapshot {
  return {
    user: {
      id: "11111111-1111-4111-8111-111111111111",
      displayName: "Ana Ribeiro",
    },
    companies,
    flags: { "release.dashboard": true },
    platform: { isPlatformOperator: false },
  };
}

function authenticated(
  companies: FoundationBootstrapSnapshot["companies"],
): FoundationBootstrapLoadResult {
  return { ok: true, snapshot: snapshot(companies) };
}

function deferred<T>() {
  let resolve!: (value: T) => void;

  return {
    promise: new Promise<T>((complete) => {
      resolve = complete;
    }),
    resolve,
  };
}

function SessionProbe() {
  const { bootstrap, refreshBootstrap, status } = useSession();

  return (
    <>
      <p>{status}</p>
      <p>
        {bootstrap?.companies.map((company) => company.companyName).join(",")}
      </p>
      <button onClick={() => void refreshBootstrap()} type="button">
        Atualizar bootstrap
      </button>
    </>
  );
}

function renderProvider(
  load: (signal?: AbortSignal) => Promise<FoundationBootstrapLoadResult>,
  queryClient = new QueryClient(),
) {
  render(
    <QueryClientProvider client={queryClient}>
      <SessionProvider
        bootstrapClient={{ load }}
        gateway={createMockSessionGateway()}
        queryClient={queryClient}
      >
        <SessionProbe />
      </SessionProvider>
    </QueryClientProvider>,
  );

  return queryClient;
}

describe("SessionProvider bootstrap refresh", () => {
  it("fails closed when a real bootstrap client has no authenticated cache", () => {
    const incompleteProps = {
      bootstrapClient: {
        load: async () => authenticated([alfa]),
      },
      gateway: createMockSessionGateway(),
    } as unknown as ComponentProps<typeof SessionProvider>;

    expect(() =>
      render(
        <SessionProvider {...incompleteProps}>
          <SessionProbe />
        </SessionProvider>,
      ),
    ).toThrow(
      "Bootstrap real exige QueryClient para limpar cache autenticado.",
    );
  });

  it("replaces a revoked membership atomically and clears authenticated queries", async () => {
    const user = userEvent.setup();
    const refreshed = deferred<FoundationBootstrapLoadResult>();
    const load = vi
      .fn<(signal?: AbortSignal) => Promise<FoundationBootstrapLoadResult>>()
      .mockResolvedValueOnce(authenticated([alfa, beta]))
      .mockReturnValueOnce(refreshed.promise);
    const queryClient = new QueryClient();
    queryClient.setQueryData(["company", alfa.companyId], { protected: true });

    renderProvider(load, queryClient);

    expect(await screen.findByText("Alfa,Beta")).toBeVisible();
    await user.click(
      screen.getByRole("button", { name: "Atualizar bootstrap" }),
    );

    expect(screen.getByText("pending")).toBeVisible();
    expect(
      queryClient.getQueryData(["company", alfa.companyId]),
    ).toBeUndefined();
    await act(async () => {
      refreshed.resolve(authenticated([beta]));
    });
    expect(await screen.findByText("Beta")).toBeVisible();
    expect(screen.queryByText("Alfa,Beta")).not.toBeInTheDocument();
    expect(screen.getByText("authenticated")).toBeVisible();
  });

  it("keeps the newer bootstrap when an aborted older refresh completes later", async () => {
    const user = userEvent.setup();
    const older = deferred<FoundationBootstrapLoadResult>();
    const newer = deferred<FoundationBootstrapLoadResult>();
    const load = vi
      .fn<(signal?: AbortSignal) => Promise<FoundationBootstrapLoadResult>>()
      .mockResolvedValueOnce(authenticated([alfa, beta]))
      .mockReturnValueOnce(older.promise)
      .mockReturnValueOnce(newer.promise);

    renderProvider(load);

    await screen.findByText("Alfa,Beta");
    const refresh = screen.getByRole("button", { name: "Atualizar bootstrap" });
    await user.click(refresh);
    await user.click(refresh);

    await act(async () => {
      newer.resolve(authenticated([beta]));
    });
    expect(await screen.findByText("Beta")).toBeVisible();

    await act(async () => {
      older.resolve(authenticated([alfa, beta]));
    });
    await waitFor(() => {
      expect(screen.getByText("Beta")).toBeVisible();
      expect(screen.queryByText("Alfa,Beta")).not.toBeInTheDocument();
    });
  });

  it("clears protected state and cache when a refresh confirms anonymous access", async () => {
    const user = userEvent.setup();
    const load = vi
      .fn<(signal?: AbortSignal) => Promise<FoundationBootstrapLoadResult>>()
      .mockResolvedValueOnce(authenticated([alfa, beta]))
      .mockResolvedValueOnce({
        ok: false,
        error: { kind: "anonymous", status: 401 },
      });
    const queryClient = new QueryClient();
    queryClient.setQueryData(["company", beta.companyId], { protected: true });

    renderProvider(load, queryClient);

    await screen.findByText("Alfa,Beta");
    await user.click(
      screen.getByRole("button", { name: "Atualizar bootstrap" }),
    );

    await waitFor(() => expect(screen.getByText("anonymous")).toBeVisible());
    expect(
      queryClient.getQueryData(["company", beta.companyId]),
    ).toBeUndefined();
    expect(screen.queryByText("Alfa,Beta")).not.toBeInTheDocument();
  });

  it("revalidates bootstrap once on reconnect", async () => {
    const load = vi
      .fn<(signal?: AbortSignal) => Promise<FoundationBootstrapLoadResult>>()
      .mockResolvedValueOnce(authenticated([alfa, beta]))
      .mockResolvedValueOnce(authenticated([beta]));

    renderProvider(load);

    await screen.findByText("Alfa,Beta");
    await act(async () => {
      window.dispatchEvent(new Event("online"));
    });

    expect(await screen.findByText("Beta")).toBeVisible();
    expect(load).toHaveBeenCalledTimes(2);
  });
});
