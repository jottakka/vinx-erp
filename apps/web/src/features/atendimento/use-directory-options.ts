import { useEffect, useState } from "react";
import { atendimentoClient, type PersonOption } from "./atendimento-client";

/**
 * Uma única busca de diretório por empresa compartilhada por todos os
 * pickers (filtro, responsável, solicitante, menções e observadores);
 * falha não fica cacheada para permitir retry na próxima montagem.
 */
const optionsCache = new Map<string, Promise<PersonOption[] | null>>();

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
  const [options, setOptions] = useState<PersonOption[]>([]);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    if (!companyId) return;
    let active = true;
    // Empresa nova começa sem rótulos da anterior (fail-closed): nomes são
    // vinculados à empresa consultada e nunca atravessam a troca de contexto.
    setOptions([]);
    setFailed(false);
    void loadDirectoryOptions(companyId).then((result) => {
      if (!active) return;
      if (result) setOptions(result);
      else setFailed(true);
    });
    return () => {
      active = false;
    };
  }, [companyId]);

  return { failed, options };
}
