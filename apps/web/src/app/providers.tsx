import { QueryClientProvider } from "@tanstack/react-query";
import { type ReactNode, useState } from "react";
import { createMockSessionGateway } from "../features/auth/mock-session-gateway";
import type { MockSessionGateway } from "../features/auth/session-contract";
import { SessionProvider } from "../features/auth/session-provider";
import { FeedbackProvider } from "../shared/ui/feedback-provider";
import { createQueryClient } from "./query-client";
import { VinxThemeProvider } from "./theme/vinx-theme-provider";

type AppProvidersProps = {
  children: ReactNode;
  sessionGateway?: MockSessionGateway;
};

export function AppProviders({ children, sessionGateway }: AppProvidersProps) {
  const [gateway] = useState(
    () => sessionGateway ?? createMockSessionGateway(),
  );
  const [queryClient] = useState(createQueryClient);

  return (
    <VinxThemeProvider>
      <QueryClientProvider client={queryClient}>
        <SessionProvider gateway={gateway} queryClient={queryClient}>
          <FeedbackProvider>{children}</FeedbackProvider>
        </SessionProvider>
      </QueryClientProvider>
    </VinxThemeProvider>
  );
}
