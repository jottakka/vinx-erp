import { QueryClient } from "@tanstack/react-query";
import { act, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ComponentType, ReactNode } from "react";
import { describe, expect, it, vi } from "vitest";
import type {
  BrowserSessionOperationResult,
  BrowserSessionSignOutResult,
} from "./browser-session-client";
import type {
  FoundationBootstrapGateway,
  FoundationBootstrapLoadResult,
  FoundationBootstrapSnapshot,
} from "./foundation-bootstrap-client";
import { SessionProvider, useSession } from "./session-provider";

const snapshot: FoundationBootstrapSnapshot = {
  user: {
    id: "11111111-1111-4111-8111-111111111111",
    displayName: "Ana Ribeiro",
  },
  companies: [
    {
      membershipId: "22222222-2222-4222-8222-222222222222",
      companyId: "33333333-3333-4333-8333-333333333333",
      companyName: "Alfa",
      capabilities: [{ name: "people.view", scopes: ["Company"] }],
    },
  ],
  flags: { "release.dashboard": true },
  platform: { isPlatformOperator: false },
};

type RealSessionProviderProps = {
  children: ReactNode;
  browserSessionClient: {
    signIn(
      credentials: { email: string; password: string },
      signal?: AbortSignal,
    ): Promise<BrowserSessionOperationResult>;
    signOut(signal?: AbortSignal): Promise<BrowserSessionSignOutResult>;
  };
  bootstrapClient: FoundationBootstrapGateway;
  queryClient: Pick<QueryClient, "clear" | "getQueryData" | "setQueryData">;
};

// The real-client props are the intentionally missing provider boundary.
const RealSessionProvider =
  SessionProvider as unknown as ComponentType<RealSessionProviderProps>;

type DesiredSessionContext = Omit<ReturnType<typeof useSession>, "signIn"> & {
  signIn(credentials: { email: string; password: string }): Promise<boolean>;
};

function Probe() {
  const session = useSession() as unknown as DesiredSessionContext;

  return (
    <>
      <p data-testid="status">{session.status}</p>
      <p data-testid="user">
        {session.bootstrap?.user.displayName ?? "anonymous"}
      </p>
      <p data-testid="error">{session.error ?? "none"}</p>
      <button
        onClick={() =>
          void session.signIn({
            email: "maria@example.com",
            password: "secret",
          })
        }
        type="button"
      >
        Sign in
      </button>
      <button onClick={() => void session.signOut()} type="button">
        Sign out
      </button>
    </>
  );
}

function renderRealSession(
  bootstrap: FoundationBootstrapGateway,
  browserSessionClient: RealSessionProviderProps["browserSessionClient"],
  queryClient = new QueryClient(),
) {
  render(
    <RealSessionProvider
      bootstrapClient={bootstrap}
      browserSessionClient={browserSessionClient}
      queryClient={queryClient}
    >
      <Probe />
    </RealSessionProvider>,
  );

  return queryClient;
}

function result401(): FoundationBootstrapLoadResult {
  return { ok: false, error: { kind: "anonymous", status: 401 } };
}

function result200(): FoundationBootstrapLoadResult {
  return { ok: true, snapshot };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((complete) => {
    resolve = complete;
  });
  return { promise, resolve };
}

describe("SessionProvider real browser session", () => {
  it("treats initial 401 as anonymous, then publishes only the post-sign-in bootstrap", async () => {
    const user = userEvent.setup();
    const load = vi
      .fn<(signal?: AbortSignal) => Promise<FoundationBootstrapLoadResult>>()
      .mockResolvedValueOnce(result401())
      .mockResolvedValueOnce(result200());
    const signIn = vi
      .fn<(...args: unknown[]) => Promise<BrowserSessionOperationResult>>()
      .mockResolvedValue({ ok: true });
    const signOut = vi
      .fn<() => Promise<BrowserSessionSignOutResult>>()
      .mockResolvedValue({ ok: true });

    renderRealSession({ load }, { signIn, signOut });

    expect(await screen.findByTestId("status")).toHaveTextContent("anonymous");
    await user.click(screen.getByRole("button", { name: "Sign in" }));

    await waitFor(() =>
      expect(screen.getByTestId("user")).toHaveTextContent("Ana Ribeiro"),
    );
    expect(screen.getByTestId("status")).toHaveTextContent("authenticated");
    expect(load).toHaveBeenCalledTimes(2);
    expect(signIn).toHaveBeenCalledWith(
      {
        email: "maria@example.com",
        password: "secret",
      },
      expect.any(AbortSignal),
    );
  });

  it("does not authenticate when BFF sign-in succeeds but bootstrap remains 401", async () => {
    const user = userEvent.setup();
    const load = vi
      .fn<(signal?: AbortSignal) => Promise<FoundationBootstrapLoadResult>>()
      .mockResolvedValue(result401());
    const signIn = vi
      .fn<(...args: unknown[]) => Promise<BrowserSessionOperationResult>>()
      .mockResolvedValue({ ok: true });

    renderRealSession(
      { load },
      {
        signIn,
        signOut: vi
          .fn<() => Promise<BrowserSessionSignOutResult>>()
          .mockResolvedValue({ ok: true }),
      },
    );

    await screen.findByText("anonymous");
    await user.click(screen.getByRole("button", { name: "Sign in" }));

    await waitFor(() => expect(load).toHaveBeenCalledTimes(2));
    expect(screen.getByTestId("status")).toHaveTextContent("anonymous");
    expect(screen.getByTestId("user")).toHaveTextContent("anonymous");
  });

  it("enters logout recovery on antiforgery unavailability and does not rebootstrap online", async () => {
    const user = userEvent.setup();
    const load = vi
      .fn<(signal?: AbortSignal) => Promise<FoundationBootstrapLoadResult>>()
      .mockResolvedValue(result200());
    const signOut = vi
      .fn<() => Promise<BrowserSessionSignOutResult>>()
      .mockResolvedValue({
        ok: false,
        stage: "antiforgery",
        error: {
          kind: "unavailable",
          status: 503,
          code: "identity_gate_unavailable",
        },
      });
    const queryClient = renderRealSession(
      { load },
      {
        signIn: vi
          .fn<(...args: unknown[]) => Promise<BrowserSessionOperationResult>>()
          .mockResolvedValue({ ok: true }),
        signOut,
      },
    );
    queryClient.setQueryData(["protected"], { visible: true });

    await screen.findByText("Ana Ribeiro");
    await user.click(screen.getByRole("button", { name: "Sign out" }));

    await waitFor(() =>
      expect(screen.getByTestId("status")).toHaveTextContent("logout_recovery"),
    );
    expect(screen.getByTestId("user")).toHaveTextContent("anonymous");
    expect(queryClient.getQueryData(["protected"])).toBeUndefined();
    expect(load).toHaveBeenCalledTimes(1);

    await act(async () => window.dispatchEvent(new Event("online")));
    expect(load).toHaveBeenCalledTimes(1);
    expect(signOut).toHaveBeenCalledTimes(1);
  });

  it("returns anonymous after a DELETE-stage 503 and uses the real sign-out client", async () => {
    const user = userEvent.setup();
    const load = vi
      .fn<(signal?: AbortSignal) => Promise<FoundationBootstrapLoadResult>>()
      .mockResolvedValue(result200());
    const signOut = vi
      .fn<() => Promise<BrowserSessionSignOutResult>>()
      .mockResolvedValue({
        ok: false,
        stage: "session",
        error: {
          kind: "unavailable",
          status: 503,
          code: "identity_gate_unavailable",
        },
      });

    renderRealSession(
      { load },
      {
        signIn: vi
          .fn<(...args: unknown[]) => Promise<BrowserSessionOperationResult>>()
          .mockResolvedValue({ ok: true }),
        signOut,
      },
    );

    await screen.findByText("Ana Ribeiro");
    await user.click(screen.getByRole("button", { name: "Sign out" }));

    await waitFor(() =>
      expect(screen.getByTestId("status")).toHaveTextContent("anonymous"),
    );
    expect(screen.getByTestId("user")).toHaveTextContent("anonymous");
    expect(signOut).toHaveBeenCalledTimes(1);
  });

  it("accepts a DELETE-stage 401 as anonymous and clears the authenticated cache", async () => {
    const user = userEvent.setup();
    const load = vi
      .fn<(signal?: AbortSignal) => Promise<FoundationBootstrapLoadResult>>()
      .mockResolvedValue(result200());
    const signOut = vi
      .fn<() => Promise<BrowserSessionSignOutResult>>()
      .mockResolvedValue({
        ok: false,
        stage: "session",
        error: { kind: "unauthorized", status: 401 },
      });
    const queryClient = renderRealSession(
      { load },
      {
        signIn: vi
          .fn<(...args: unknown[]) => Promise<BrowserSessionOperationResult>>()
          .mockResolvedValue({ ok: true }),
        signOut,
      },
    );
    queryClient.setQueryData(["protected"], { visible: true });

    await screen.findByText("Ana Ribeiro");
    await user.click(screen.getByRole("button", { name: "Sign out" }));

    await waitFor(() =>
      expect(screen.getByTestId("status")).toHaveTextContent("anonymous"),
    );
    expect(screen.getByTestId("user")).toHaveTextContent("anonymous");
    expect(screen.getByTestId("status")).not.toHaveTextContent(
      "logout_recovery",
    );
    expect(queryClient.getQueryData(["protected"])).toBeUndefined();
    expect(signOut).toHaveBeenCalledTimes(1);
  });

  it("does not restore an old bootstrap response that completes after definitive logout", async () => {
    const user = userEvent.setup();
    const oldBootstrap = deferred<FoundationBootstrapLoadResult>();
    const observedSignals: AbortSignal[] = [];
    const load = vi.fn((signal?: AbortSignal) => {
      if (signal) observedSignals.push(signal);
      return oldBootstrap.promise;
    });
    const signOut = vi
      .fn<() => Promise<BrowserSessionSignOutResult>>()
      .mockResolvedValue({ ok: true });

    renderRealSession(
      { load },
      {
        signIn: vi
          .fn<(...args: unknown[]) => Promise<BrowserSessionOperationResult>>()
          .mockResolvedValue({ ok: true }),
        signOut,
      },
    );

    await user.click(screen.getByRole("button", { name: "Sign out" }));
    await waitFor(() =>
      expect(screen.getByTestId("status")).toHaveTextContent("anonymous"),
    );
    expect(observedSignals).toHaveLength(1);
    expect(observedSignals[0]?.aborted).toBe(true);

    await act(async () => {
      oldBootstrap.resolve(result200());
    });
    expect(screen.getByTestId("status")).toHaveTextContent("anonymous");
    expect(screen.getByTestId("user")).toHaveTextContent("anonymous");
    expect(signOut).toHaveBeenCalledTimes(1);
  });

  it("allows an explicit logout retry from recovery while online does not bootstrap", async () => {
    const user = userEvent.setup();
    const load = vi
      .fn<(signal?: AbortSignal) => Promise<FoundationBootstrapLoadResult>>()
      .mockResolvedValue(result200());
    const signOut = vi
      .fn<() => Promise<BrowserSessionSignOutResult>>()
      .mockResolvedValueOnce({
        ok: false,
        stage: "antiforgery",
        error: {
          kind: "unavailable",
          status: 503,
          code: "identity_gate_unavailable",
        },
      })
      .mockResolvedValueOnce({ ok: true });

    renderRealSession(
      { load },
      {
        signIn: vi
          .fn<(...args: unknown[]) => Promise<BrowserSessionOperationResult>>()
          .mockResolvedValue({ ok: true }),
        signOut,
      },
    );

    await screen.findByText("Ana Ribeiro");
    await user.click(screen.getByRole("button", { name: "Sign out" }));
    await waitFor(() =>
      expect(screen.getByTestId("status")).toHaveTextContent("logout_recovery"),
    );

    await act(async () => window.dispatchEvent(new Event("online")));
    expect(load).toHaveBeenCalledTimes(1);
    expect(signOut).toHaveBeenCalledTimes(1);

    await user.click(screen.getByRole("button", { name: "Sign out" }));
    await waitFor(() =>
      expect(screen.getByTestId("status")).toHaveTextContent("anonymous"),
    );
    expect(screen.getByTestId("user")).toHaveTextContent("anonymous");
    expect(signOut).toHaveBeenCalledTimes(2);
    expect(load).toHaveBeenCalledTimes(1);
  });
});
