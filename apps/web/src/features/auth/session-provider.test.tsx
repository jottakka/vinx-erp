import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { createMockSessionGateway } from "./mock-session-gateway";
import { SessionProvider, useSession } from "./session-provider";

function SessionProbe() {
  const session = useSession();
  const [signOutResult, setSignOutResult] = useState("not requested");

  return (
    <>
      <p>{session.status}</p>
      <p>{session.bootstrap?.user.name ?? "anonymous"}</p>
      <p>{session.error ?? "no error"}</p>
      <output>{signOutResult}</output>
      <button onClick={() => session.signIn("maria")} type="button">
        Sign in
      </button>
      <button
        onClick={async () => setSignOutResult(String(await session.signOut()))}
        type="button"
      >
        Sign out
      </button>
    </>
  );
}

describe("SessionProvider", () => {
  it("exposes the bootstrap after a successful sign-in", async () => {
    const user = userEvent.setup();

    render(
      <SessionProvider gateway={createMockSessionGateway()}>
        <SessionProbe />
      </SessionProvider>,
    );

    await screen.findByText("anonymous");
    await user.click(screen.getByRole("button", { name: "Sign in" }));

    expect(await screen.findByText("Maria")).toBeVisible();
    expect(screen.getByText("authenticated")).toBeVisible();
  });

  it("starts a fresh provider anonymous after a simulated reload", async () => {
    const signedInGateway = createMockSessionGateway();
    await signedInGateway.login("maria");

    const firstRender = render(
      <SessionProvider gateway={signedInGateway}>
        <SessionProbe />
      </SessionProvider>,
    );

    expect(await screen.findByText("Maria")).toBeVisible();
    firstRender.unmount();

    render(
      <SessionProvider gateway={createMockSessionGateway()}>
        <SessionProbe />
      </SessionProvider>,
    );

    expect(await screen.findByText("anonymous")).toBeVisible();
  });

  it("keeps the authenticated session and returns false when logout fails", async () => {
    const user = userEvent.setup();
    const gateway = createMockSessionGateway({
      beforeLogout: async () => {
        throw new Error("Não foi possível sair");
      },
    });

    render(
      <SessionProvider gateway={gateway}>
        <SessionProbe />
      </SessionProvider>,
    );

    await screen.findByText("anonymous");
    await user.click(screen.getByRole("button", { name: "Sign in" }));
    await screen.findByText("authenticated");
    await user.click(screen.getByRole("button", { name: "Sign out" }));

    expect(
      await screen.findByText(
        "Não foi possível sair do aplicativo. Tente novamente.",
      ),
    ).toBeVisible();
    expect(screen.getByText("authenticated")).toBeVisible();
    expect(screen.getByText("Maria")).toBeVisible();
    expect(screen.getByRole("status")).toHaveTextContent("false");
  });
});
