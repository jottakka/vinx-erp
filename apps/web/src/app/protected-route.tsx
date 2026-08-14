import { Box, Skeleton, Stack, Typography } from "@mui/material";
import type { ReactNode } from "react";
import { Navigate, useLocation } from "react-router-dom";
import { useSession } from "../features/auth/session-provider";
import { isKnownAppPath } from "../shared/routing/app-paths";

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
          bgcolor: "background.default",
          minHeight: "100dvh",
          p: { sm: 4, xs: 2 },
        }}
      >
        <Stack role="status" spacing={2} sx={{ maxWidth: "36rem" }}>
          <Typography>Carregando aplicativo…</Typography>
          <Skeleton aria-hidden="true" variant="rectangular" />
          <Skeleton aria-hidden="true" height={24} variant="rectangular" />
          <Skeleton aria-hidden="true" variant="rectangular" />
          <Skeleton aria-hidden="true" height={160} variant="rectangular" />
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
