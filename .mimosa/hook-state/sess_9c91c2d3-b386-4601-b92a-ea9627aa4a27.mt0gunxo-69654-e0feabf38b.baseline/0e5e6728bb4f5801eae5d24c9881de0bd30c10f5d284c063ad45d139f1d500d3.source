import PaletteOutlinedIcon from "@mui/icons-material/PaletteOutlined";
import { Divider, IconButton, Menu, MenuItem } from "@mui/material";
import { useColorScheme } from "@mui/material/styles";
import { useState } from "react";
import { useVisualPreferences } from "../../app/theme/vinx-theme-provider";
import type { Density } from "../../app/theme/visual-preferences";

type ColorMode = "light" | "dark";

const menuId = "vinx-appearance-menu";
const triggerId = "vinx-appearance-menu-trigger";

export function AppearanceMenu() {
  const [anchorEl, setAnchorEl] = useState<HTMLElement | null>(null);
  const { mode, setMode } = useColorScheme();
  const { density, setDensity } = useVisualPreferences();
  const open = Boolean(anchorEl);

  function openMenu(event: React.MouseEvent<HTMLElement>) {
    setAnchorEl(event.currentTarget);
  }

  function closeMenu() {
    setAnchorEl(null);
  }

  function selectMode(nextMode: ColorMode) {
    setMode(nextMode);
    closeMenu();
  }

  function selectDensity(nextDensity: Density) {
    setDensity(nextDensity);
    closeMenu();
  }

  return (
    <>
      <IconButton
        aria-controls={open ? menuId : undefined}
        aria-expanded={open ? "true" : undefined}
        aria-haspopup="menu"
        aria-label="Abrir preferências visuais"
        id={triggerId}
        onClick={openMenu}
      >
        <PaletteOutlinedIcon />
      </IconButton>
      <Menu
        anchorEl={anchorEl}
        id={menuId}
        onClose={closeMenu}
        open={open}
        slotProps={{
          list: {
            "aria-labelledby": triggerId,
          },
        }}
      >
        <MenuItem
          aria-checked={(mode ?? "light") === "light"}
          onClick={() => selectMode("light")}
          role="menuitemradio"
          selected={(mode ?? "light") === "light"}
        >
          Tema: claro
        </MenuItem>
        <MenuItem
          aria-checked={mode === "dark"}
          onClick={() => selectMode("dark")}
          role="menuitemradio"
          selected={mode === "dark"}
        >
          Tema: escuro
        </MenuItem>
        <Divider sx={{ my: 0.5 }} variant="middle" />
        <MenuItem
          aria-checked={density === "comfortable"}
          onClick={() => selectDensity("comfortable")}
          role="menuitemradio"
          selected={density === "comfortable"}
        >
          Densidade: confortável
        </MenuItem>
        <MenuItem
          aria-checked={density === "compact"}
          onClick={() => selectDensity("compact")}
          role="menuitemradio"
          selected={density === "compact"}
        >
          Densidade: compacta
        </MenuItem>
      </Menu>
    </>
  );
}
