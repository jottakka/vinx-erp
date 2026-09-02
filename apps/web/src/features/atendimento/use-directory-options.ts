import { useEffect, useState } from "react";
import { atendimentoClient, type PersonOption } from "./atendimento-client";

/**
 * Uma única busca de diretório por empresa compartilhada por todos os
 * pickers (filtro, responsável, solicitante, menções e observadores);
 * falha não fica cacheada para permitir retry na próxima montagem.
 */
const optionsCache = new Map<string, Promise<PersonOption[] | null>>();

/**
 * Limpa o cache quando a sessão muda (login, logout ou troca de identidade):
 * opções de diretório pertencem à sessão que as carregou e nunca vazam para
 * a seguinte. Chamado pelo SessionProvider junto com queryClient.clear().
 */
const invalidationListeners = new Set<() => void>();

export function resetDirectoryOptionsCache() {
  optionsCache.clear();
  // Hooks montados mantêm estado próprio: sem isto, nomes carregados pela
  // sessão anterior continuam visíveis após troca de identidade mantendo a
  // mesma empresa (bloqueio de revisão).
  for (const notify of invalidationListeners) {
    notify();
  }
}

function subscribeDirectoryInvalidation(notify: () => void): () => void {
  invalidationListeners.add(notify);
  return () => {
    invalidationListeners.delete(notify);
  };
}

function loadDirectoryOptions(
  companyId: string,
): Promise<PersonOption[] | null> {
  const cached = optionsCache.get(companyId);
  if (cached) return cached;
  const pending = atendimentoClient
    .directoryOptions(companyId)
    .then((result) => {
      if (!result) optionsCache.delete(companyId);
      return result;
    });
  optionsCache.set(companyId, pending);
  return pending;
}

export function useDirectoryOptions(companyId: string): {
  failed: boolean;
  options: PersonOption[];
} {
  // Opções são vinculadas à empresa que as produziu: qualquer render com
  // companyId diferente da dona (troca ou esvaziamento) expõe lista vazia
  // fail-closed, sem esperar efeito.
  const [owned, setOwned] = useState<{
    companyId: string;
    options: PersonOption[];
  }>({
    companyId: "",
    options: [],
  });
  const [failedOwner, setFailedOwner] = useState<string | null>(null);
  const [reloadToken, setReloadToken] = useState(0);

  useEffect(
    () =>
      subscribeDirectoryInvalidation(() => {
        setOwned({ companyId: "", options: [] });
        setFailedOwner(null);
        setReloadToken((token) => token + 1);
      }),
    [],
  );

  useEffect(() => {
    if (!companyId) return;
    let active = true;
    void loadDirectoryOptions(companyId).then((result) => {
      if (!active) return;
      if (result) setOwned({ companyId, options: result });
      else setFailedOwner(companyId);
    });
    return () => {
      active = false;
    };
    // biome-ignore lint/correctness/useExhaustiveDependencies: reloadToken é o gatilho
    // de invalidação de sessão — reset limpa o estado e dispara recarga.
  }, [companyId, reloadToken]);

  const options = owned.companyId === companyId ? owned.options : [];
  const failed = failedOwner === companyId && options.length === 0;
  return { failed, options };
}
