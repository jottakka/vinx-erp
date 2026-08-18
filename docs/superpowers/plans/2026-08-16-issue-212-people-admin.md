# Pessoas por empresa — Plano de implementação

> **Para agentes:** use `superpowers:subagent-driven-development` para executar cada tarefa, com teste RED antes do código e revisão independente após a tarefa.

**Objetivo:** tornar `/pessoas` uma tela protegida de diretório e reserva local de funcionário para uma empresa explicitamente selecionada.

**Arquitetura:** o frontend consome somente os contratos já publicados por #229. Um cliente de domínio fixa os caminhos same-origin, valida JSON com Zod e obtém CSRF efêmero antes do POST. A tela deriva empresas elegíveis do bootstrap somente para UX, enquanto a autorização continua exclusiva do endpoint, serviço, função SQL e RLS.

**Stack:** React 19, React Router, MUI, TanStack Query, React Hook Form, Zod, MSW, Vitest e Testing Library já presentes.

## Restrições globais

- Base da branch: `codex/issue-211-employee-directory` (#229); PR filha em stack.
- Não tocar Paguai, Atendimento, Kanban, backend, migrations, catálogo de capabilities, provider, outbox, convite, aceite, AAL2/TOTP ou storage.
- Usar somente caminhos relativos allow-listed, `credentials: "same-origin"`, `mode: "same-origin"`, `redirect: "error"` e `cache: "no-store"`; nenhum token, URL configurável, Supabase/Admin API ou browser storage.
- A seleção de empresa é explícita: sem seleção ou sem `foundation.employees.create` com escopo `Company`, não há GET nem POST de diretório.
- O browser não concede acesso. `401` e `403` removem a query da empresa e revalidam bootstrap; respostas/payloads desconhecidos falham fechados e nunca chegam à UI.
- POST não tem retry automático, não tem inserção otimista e preserva o mesmo `requestId` e payload somente para retry explícito após resultado ambíguo.
- Não mostrar e-mail persistido, `userId`, `membershipId`, papel, convite, provider, payload bruto ou detalhes técnicos; e-mail fica somente no formulário/tentativa em memória.
- Documentação em português; sem dependência nova; Paguai/Atendimento/Kanban permanecem fora do diff.

---

### Task 1: Cliente seguro do diretório

**Arquivos:**

- Criar: `apps/web/src/features/people/employee-directory-client.ts`
- Testar: `apps/web/src/features/people/employee-directory-client.test.ts`

**Produz:**

```ts
export type EmployeeDirectoryItem = {
  employeeId: string;
  membershipId: string;
  displayName: string;
};

export type EmployeeDirectoryGateway = {
  list(companyId: string, signal?: AbortSignal): Promise<EmployeeDirectoryListResult>;
  create(command: EmployeeDirectoryCreateCommand, signal?: AbortSignal): Promise<EmployeeDirectoryCreateResult>;
};

export function createEmployeeDirectoryClient(
  fetchImplementation?: typeof fetch,
): EmployeeDirectoryGateway;
```

- [ ] **Passo 1: escrever testes RED do contrato HTTP**

Cobrir com MSW um GET de lista válido, POST que primeiro busca o antiforgery e envia `X-CSRF-TOKEN`, e corpo estrito com `requestId`, `companyId`, `displayName` e `email`. Confirmar que o POST retorna somente um estado `created` ou `replay`, sem expor IDs à tela.

```ts
expect(fetchSpy.mock.calls.map(([path]) => path)).toEqual([
  "/foundation/identity/browser-session/antiforgery",
  "/foundation/companies/<company-id>/employees",
]);
expect(post.headers.get("X-CSRF-TOKEN")).toBe("fresh-token");
```

Rodar:

```bash
npm test -- src/features/people/employee-directory-client.test.ts
```

Esperado: falha porque o cliente não existe.

- [ ] **Passo 2: escrever testes RED de falha fechada**

Adicionar casos para UUID/comando inválido sem chamada HTTP, JSON de lista/resposta de criação incompatível, `401`, `403`, `409` com `employee_conflict` e `idempotency_conflict`, `429`, `503`, abort e falha de rede. Cada resultado usa somente categoria allow-listed e `X-Correlation-ID` UUID válido; texto/payload desconhecido não é repassado.

- [ ] **Passo 3: implementar o mínimo**

Implementar o cliente com Zod estrito, UUID canônico, caminhos relativos internos e `fetch` same-origin. O POST deve obter um token antiforgery novo em memória, fazer uma única chamada de criação e normalizar somente estados documentados. Não extrair uma abstração genérica de transporte: os dois caminhos do diretório são os únicos consumidores desta lógica.

- [ ] **Passo 4: verificar GREEN e formatar**

Rodar o arquivo de teste, depois `npm run lint -- --write=false` somente se compatível com o script atual, e `npm run format:check` para os arquivos alterados. Registrar contagem e resultado no relatório da tarefa.

### Task 2: Tela Pessoas com seleção explícita e tentativa idempotente

**Arquivos:**

- Criar: `apps/web/src/features/people/people-page.tsx`
- Testar: `apps/web/src/features/people/people-page.test.tsx`

**Consome:** `EmployeeDirectoryGateway`, `useSession`, `useQuery`, `useMutation`, `useQueryClient`, `useForm` e `zodResolver`.

**Produz:**

```tsx
export function PeoplePage({
  gateway = createEmployeeDirectoryClient(),
  createRequestId = crypto.randomUUID,
}: PeoplePageProps): ReactElement;
```

- [ ] **Passo 1: escrever testes RED de acesso e leitura**

Renderizar a página com bootstrap controlado. Sem empresa com `foundation.employees.create` + `Company`, verificar mensagem genérica e zero chamadas ao gateway. Com empresas elegíveis, verificar que nenhuma lista é carregada antes da seleção e que a seleção explícita carrega somente a empresa escolhida, exibindo apenas `displayName`.

- [ ] **Passo 2: escrever testes RED de reserva**

Usar `userEvent` para verificar validação local de nome/e-mail sem chamada, criação `201`/replay `200`, botão desabilitado durante a mutation e atualização de lista apenas após revalidação bem-sucedida do bootstrap. Cobrir conflito não enumerante, `401`/`403` com remoção da query e revalidação, e resultado ambíguo com botão de retry que reusa o mesmo `requestId` e payload.

- [ ] **Passo 3: implementar o mínimo**

Derivar empresas elegíveis do snapshot sem inferir empresa ativa. Usar `react-hook-form` + Zod para `displayName` trim de 1 a 200 caracteres e e-mail trim de até 320 caracteres. Guardar tentativa somente em estado local; mudança de empresa ou de campo descarta a tentativa. Não criar inserção otimista, retry automático, estado global, storage, URL com empresa ou exibição de identificadores opacos. Após sucesso, revalidar bootstrap e, somente se válido, invalidar/refazer a query da empresa.

- [ ] **Passo 4: verificar GREEN e formatar**

Rodar o teste da página e o teste do cliente em série. Confirmar por asserções que nome hostil é renderizado como texto e e-mail não aparece na lista nem nos alertas.

### Task 3: Rota, documentação e verificação da fatia

**Arquivos:**

- Modificar: `apps/web/src/app/app-route-manifest.tsx`
- Modificar: `apps/web/src/app/app-route-manifest.test.tsx`
- Criar: `docs/manuals/frontend/employee-directory.md`
- Criar: `docs/verification/frontend-employee-directory-test-cases.md`

**Consome:** `PeoplePage` da tarefa 2.

- [ ] **Passo 1: escrever teste RED da rota real**

Estender o teste do manifesto para provar que somente `/pessoas` usa `PeoplePage`; caminhos Paguai, Atendimento e Kanban continuam apontando para `FeaturePreparationPage` e não são alterados.

- [ ] **Passo 2: implementar a ligação mínima**

Substituir apenas o render de `/pessoas` em `app-route-manifest.tsx`. Não alterar `app-paths.ts`, `AppShell`, `router.tsx`, Home, clientes de sessão ou protótipos; isso mantém a superfície compartilhada com os outros módulos no mínimo necessário.

- [ ] **Passo 3: documentar o comportamento durável**

No manual, explicar seleção explícita, capability como filtro de UX, CSRF transitório, retry manual idempotente, dados mostrados e adiamentos. No manifesto de verificação, mapear sucesso, validação, acesso, estado/erros e concorrência aos testes concretos, incluindo a condição de que Paguai/Atendimento/Kanban não mudam.

- [ ] **Passo 4: verificar GREEN**

Rodar os testes de rota, página e cliente; executar `npm run lint`, `npm run format:check` e `npm run build`. Fazer inspeção visual local de `/pessoas` com sessão de teste ou fixture controlada antes da revisão.

## Revisão e publicação

- Revisão de escopo/UI por Terra e revisão de sessão, CSRF, tenant, idempotência e dados expostos por Sol.
- Congelar código, executar `./scripts/verify-public.sh` uma vez imediatamente antes do push e abrir PR draft empilhada sobre #229 com a exceção de tamanho, se a contagem manual final exceder 800 linhas.
