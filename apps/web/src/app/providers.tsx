import { QueryClientProvider } from "@tanstack/react-query";
import { type ReactNode, useState } from "react";
import { createMockSessionGateway } from "../features/auth/mock-session-gateway";
import type { MockSessionGateway } from "../features/auth/session-contract";
import { SessionProvider } from "../features/auth/session-provider";
import { createQueryClient } from "./query-client";

type AppProvidersProps = {
  children: ReactNode;
  sessionGateway?: MockSessionGateway;
};

export function AppProviders({ children, sessionGateway }: AppProvidersProps) {
  const [gateway] = useState(
    () => sessionGateway ?? createMockSessionGateway(),
  );

  return (
    <QueryClientProvider client={createQueryClient()}>
      <SessionProvider gateway={gateway}>{children}</SessionProvider>
    </QueryClientProvider>
  );
}
