import { MutationObserver, onlineManager } from "@tanstack/react-query";
import { describe, expect, it, vi } from "vitest";
import { createQueryClient } from "./query-client";

describe("createQueryClient", () => {
  it("never retries mutations automatically", () => {
    const defaults = createQueryClient().getDefaultOptions();

    expect(defaults.mutations?.retry).toBe(false);
    expect(defaults.mutations?.networkMode).toBe("always");
  });

  it("executes an offline mutation once, fails it, and does not resume it when online", async () => {
    const wasOnline = onlineManager.isOnline();
    const queryClient = createQueryClient();
    const mutationFn = vi.fn().mockRejectedValue(new Error("offline mutation"));
    const observer = new MutationObserver(queryClient, { mutationFn });

    queryClient.mount();
    onlineManager.setOnline(false);

    try {
      const mutationResult = observer.mutate("payload").catch((error) => error);

      await vi.waitFor(() => expect(mutationFn).toHaveBeenCalledTimes(1), {
        interval: 10,
        timeout: 100,
      });
      await expect(mutationResult).resolves.toMatchObject({
        message: "offline mutation",
      });

      onlineManager.setOnline(true);
      await new Promise((resolve) => setTimeout(resolve, 0));

      expect(mutationFn).toHaveBeenCalledTimes(1);
    } finally {
      observer.reset();
      queryClient.unmount();
      onlineManager.setOnline(wasOnline);
    }
  });
});
