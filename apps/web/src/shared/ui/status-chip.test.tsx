import CheckCircleOutlinedIcon from "@mui/icons-material/CheckCircleOutlined";
import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { VinxThemeProvider } from "../../app/theme/vinx-theme-provider";
import { StatusChip } from "./status-chip";

describe("StatusChip", () => {
  it("renders explicit semantic status text and tone", () => {
    render(
      <VinxThemeProvider>
        <StatusChip
          icon={<CheckCircleOutlinedIcon data-testid="status-icon" />}
          label="Concluído"
          tone="success"
        />
      </VinxThemeProvider>,
    );

    const chip = screen.getByText("Concluído").closest(".MuiChip-root");
    expect(chip).toHaveClass("MuiChip-colorSuccess");
    expect(screen.getByTestId("status-icon")).toBeVisible();
  });
});
