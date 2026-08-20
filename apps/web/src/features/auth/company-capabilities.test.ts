import { describe, expect, it } from "vitest";
import { hasCompanyCapability } from "./company-capabilities";

describe("hasCompanyCapability", () => {
  it("accepts the requested capability only at company scope", () => {
    expect(
      hasCompanyCapability(
        {
          capabilities: [{ name: "people.view", scopes: ["Team", "Company"] }],
        },
        "people.view",
      ),
    ).toBe(true);
  });

  it("does not widen a capability from another scope or name", () => {
    const company = {
      capabilities: [
        { name: "people.view", scopes: ["Team"] },
        { name: "payments.view", scopes: ["Company"] },
      ],
    } as const;

    expect(hasCompanyCapability(company, "people.view")).toBe(false);
    expect(hasCompanyCapability(company, "payments.approve")).toBe(false);
  });

  it("rejects an absent company", () => {
    expect(hasCompanyCapability(undefined, "people.view")).toBe(false);
  });
});
