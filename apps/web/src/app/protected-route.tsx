import { Box, CircularProgress, Stack, Typography } from "@mui/material";
import type { ReactNode } from "react";
import { Navigate, useLocation } from "react-router-dom";
import { useSession } from "../features/auth/session-provider";
import { isKnownAppPath } from "./app-navigation";

type ProtectedRouteProps = {
  children: ReactNode;
};

export function ProtectedRoute({ children }: ProtectedRouteProps) {
  const { bootstrap, status } = useSession();
  const location = useLocation();

  if (status === "pending") {
    return (
      <Box
        aria-busy="true"
        component="main"
        sx={{
          alignItems: "center",
          bgcolor: "background.default",
          display: "flex",
          justifyContent: "center",
          minHeight: "100dvh",
          p: 2,
        }}
      >
        <Stack role="status" spacing={2} sx={{ alignItems: "center" }}>
          <CircularProgress aria-hidden="true" />
          <Typography>Carregando aplicativo…</Typography>
        </Stack>
      </Box>
    );
  }

  if (!bootstrap) {
    const returnPath =
      !location.search && !location.hash && isKnownAppPath(location.pathname)
        ? { returnPath: location.pathname }
        : undefined;

    return <Navigate replace state={returnPath} to="/login" />;
  }

  return children;
}
