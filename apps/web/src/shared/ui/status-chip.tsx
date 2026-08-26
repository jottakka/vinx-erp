import { Chip, type ChipProps } from "@mui/material";
import type { ReactElement } from "react";

export type StatusTone = Exclude<NonNullable<ChipProps["color"]>, "inherit">;

export type StatusChipProps = {
  icon?: ReactElement;
  label: string;
  tone: StatusTone;
};

export function StatusChip({ icon, label, tone }: StatusChipProps) {
  return <Chip color={tone} icon={icon} label={label} size="small" />;
}
