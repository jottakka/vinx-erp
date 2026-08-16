# Plano — #16 transporte BFF web

**Objetivo:** dar ao web um cliente pequeno para as três operações BFF de
sessão, sempre pela mesma origem e sem acesso direto a provider.

**Dependências:** #220 (BFF) e #222 (correlação de resposta), ambas como pais
na stack. **Consumidores posteriores:** #90 mapeia bootstrap; #117 conecta
sessão e tela real.

**Escopo e decisões**

- Usar `fetch` nativo e Zod já presente; não adicionar SDK, gerador, cache ou
  wrapper de URL arbitrária.
- Exportar apenas `signIn`, `signOut` e a obtenção de antiforgery necessária
  internamente. Caminhos são literais `/foundation/...`; nenhum header de
  autorização ou correlação sai do browser.
- Cada mutação busca CSRF e faz uma única chamada. Não há retry, fila, replay,
  storage de token ou listener de reconexão.
- Normalizar falhas em status/código documentado e UUID canônico de correlação,
  sem texto bruto, URL, stack ou campos desconhecidos.
- Configurar somente o proxy de desenvolvimento Vite para API loopback. O
  ingress same-origin de produção permanece responsabilidade de deployment.

**Estimativa:** 9–11 arquivos, 360–520 linhas manuais; sem API C#, migração,
lockfile, provider, tela, SessionProvider ou TanStack Query.

**BDD e verificação**

| Classe | Dado / Quando / Então | Evidência planejada |
|---|---|---|
| sucesso | Dada credencial válida, quando `signIn` é chamado, então CSRF precede uma única mutação same-origin | Vitest + MSW |
| validação/erro | Dado corpo/correlação/resposta inválidos, quando chegam, então a falha normalizada não vaza detalhes | Vitest + MSW |
| acesso | Dado caminho absoluto, protocolo-relativo ou header proibido, quando tentado, então `fetch` não é chamado | Vitest |
| estado/exceção | Dado CSRF falho, cancelamento ou `204`, quando a operação termina, então não há mutação/replay ou parse indevido | Vitest + MSW |
| concorrência | Dadas mutações paralelas e respostas em ordem inversa, quando concluem, então tokens e correlações não se cruzam | Vitest + MSW |

**Passos**

1. Criar testes RED e o contrato local do transporte.
2. Implementar o cliente restrito e schemas Zod.
3. Adicionar proxy Vite loopback e testes de configuração.
4. Documentar uso/limites, revisar, congelar, executar o gate público uma vez e
   publicar a filha draft da #222.

**Adiamentos:** mapeamento de bootstrap (#90); sessão, UI e corridas de
navegação (#117); MFA, refresh, recuperação e logout por dispositivo; proxy
TLS/inress de produção.
