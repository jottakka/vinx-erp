import { QueryClient } from "@tanstack/react-query";

export function createQueryClient() {
  return new QueryClient({
    defaultOptions: {
      mutations: {
        networkMode: "always",
        retry: false,
      },
      queries: {
        retry: 1,
        staleTime: 30_000,
      },
    },
  });
}
