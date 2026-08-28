import { QueryClientProvider } from "@tanstack/react-query";
import { type ReactNode, useState } from "react";
import { createBrowserSessionClient } from "../features/auth/browser-session-client";
import { createFoundationBootstrapClient } from "../features/auth/foundation-bootstrap-client";
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
  const [queryClient] = useState(createQueryClient);
  const [browserSessionClient] = useState(createBrowserSessionClient);
  const [bootstrapClient] = useState(createFoundationBootstrapClient);

  const session = sessionGateway ? (
    <SessionProvider gateway={sessionGateway}>
      <FeedbackProvider>{children}</FeedbackProvider>
    </SessionProvider>
  ) : (
    <SessionProvider
      bootstrapClient={bootstrapClient}
      browserSessionClient={browserSessionClient}
      queryClient={queryClient}
    >
      <FeedbackProvider>{children}</FeedbackProvider>
    </SessionProvider>
  );

  return (
    <VinxThemeProvider>
      <QueryClientProvider client={queryClient}>{session}</QueryClientProvider>
    </VinxThemeProvider>
  );
}
