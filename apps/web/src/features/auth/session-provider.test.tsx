import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMockSessionGateway } from "./mock-session-gateway";
import { SessionProvider, useSession } from "./session-provider";

function SessionProbe() {
  const session = useSession();

  return (
    <>
      <p>{session.status}</p>
      <p>{session.bootstrap?.user.name ?? "anonymous"}</p>
      <button onClick={() => session.signIn("maria")} type="button">
        Sign in
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
});
