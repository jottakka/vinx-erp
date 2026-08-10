import { describe, expect, it } from "vitest";
import { mockSessionProfiles } from "./mock-session-fixtures";
import { createMockSessionGateway } from "./mock-session-gateway";

function createDeferred<T>() {
  let resolve!: (value: T) => void;

  return {
    promise: new Promise<T>((complete) => {
      resolve = complete;
    }),
    resolve,
  };
}

describe("MockSessionGateway", () => {
  it("keeps Gama as an explicit denied source fixture while projecting only Alfa and Beta", async () => {
    const sourceCompanies = Reflect.get(
      mockSessionProfiles.maria,
      "companies",
    ) as ReadonlyArray<Readonly<Record<string, unknown>>>;
    const gateway = createMockSessionGateway();

    await gateway.login("maria");

    expect(sourceCompanies).toContainEqual(
      expect.objectContaining({ access: "denied", name: "Gama" }),
    );
    await expect(gateway.getBootstrap()).resolves.toMatchObject({
      companies: [{ name: "Alfa" }, { name: "Beta" }],
    });
  });

  it("returns Alfa and Beta but never Gama for the authorized prototype profile", async () => {
    const gateway = createMockSessionGateway();

    await gateway.login("maria");

    const bootstrap = await gateway.getBootstrap();

    expect(bootstrap).toMatchObject({
      companies: [{ name: "Alfa" }, { name: "Beta" }],
    });
    expect(bootstrap?.companies.map((company) => company.name)).not.toContain(
      "Gama",
    );
  });

  it("rejects an unavailable profile without creating a session", async () => {
    const gateway = createMockSessionGateway();

    await expect(gateway.login("missing-profile")).rejects.toThrow(
      "Perfil de desenvolvimento indisponível",
    );
    await expect(gateway.getBootstrap()).resolves.toBeNull();
  });

  it("clears the current session on logout", async () => {
    const gateway = createMockSessionGateway();

    await gateway.login("maria");
    await gateway.logout();

    await expect(gateway.getBootstrap()).resolves.toBeNull();
  });

  it("starts each fresh gateway without session state", async () => {
    const signedInGateway = createMockSessionGateway();
    await signedInGateway.login("maria");

    const freshGateway = createMockSessionGateway();

    await expect(freshGateway.getBootstrap()).resolves.toBeNull();
  });

  it("keeps the state from the last completed sign-in or sign-out action", async () => {
    const gateway = createMockSessionGateway();

    await gateway.login("maria");
    await gateway.logout();
    await gateway.login("maria");

    await expect(gateway.getBootstrap()).resolves.toMatchObject({
      user: { name: "Maria" },
    });
  });

  it("keeps the state from the last completed overlapping login or logout action", async () => {
    const loginCompletion = createDeferred<void>();
    const logoutCompletion = createDeferred<void>();
    const gateway = createMockSessionGateway({
      beforeLogin: () => loginCompletion.promise,
      beforeLogout: () => logoutCompletion.promise,
    });

    const pendingLogin = gateway.login("maria");
    const pendingLogout = gateway.logout();

    logoutCompletion.resolve();
    await pendingLogout;
    loginCompletion.resolve();
    await pendingLogin;

    await expect(gateway.getBootstrap()).resolves.toMatchObject({
      user: { name: "Maria" },
    });
  });
});
