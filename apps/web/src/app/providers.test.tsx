import { render, screen } from "@testing-library/react";
import { AppProviders } from "./providers";

it("renders application content within the shell", () => {
  render(
    <AppProviders>
      <p>Vinx shell</p>
    </AppProviders>,
  );

  expect(screen.getByText("Vinx shell")).toBeInTheDocument();
});
