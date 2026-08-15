# Plano de implementação — simplificação backend-first

> **Objetivo:** retirar coordenação prematura do agente, preservar o bloqueio
> fail-closed e deixar uma ordem executável para as APIs que liberarão o
> trabalho paralelo do frontend.

**Desenho aprovado:**
`docs/superpowers/specs/2026-08-15-backend-first-simplification.md`.

## Tarefa 1 — tornar a política package-first explícita

**Arquivos:**

- modificar `AGENTS.md`;
- criar `docs/handoffs/2026-08-15-backend-api-fe-priorities.md`.

**Passos:**

1. Registrar biblioteca/framework consolidado como primeira opção quando há
   uso atual e ganho claro.
2. Exigir justificativa curta para código próprio que substitua capacidade
   mantida e madura.
3. Registrar ordem, dependências e faixas paralelizáveis de backend, frontend,
   MCP, provedores e guardrails.

## Tarefa 2 — remover o runtime de confirmação não hospedado

**Arquivos:**

- excluir `src/AI/Vinx.Erp.AI/HarnessConfirmationCoordinator.cs`;
- reduzir `src/AI/Vinx.Erp.AI/HarnessApprovalGate.cs` à avaliação;
- substituir `src/AI/Vinx.Erp.AI/HarnessExecutionRequestIdSource.cs` por um
  feixe opaco mínimo;
- simplificar `src/AI/Vinx.Erp.AI/HarnessProbeTools.cs`;
- simplificar `src/AI/Vinx.Erp.AI/VinxHarnessFactory.cs`;
- simplificar `src/AI/Vinx.Erp.AI/VinxToolPolicy.cs`;
- remover a referência Foundation de `src/AI/Vinx.Erp.AI/Vinx.Erp.AI.csproj`.

**Passos:**

1. Ajustar primeiro `HarnessApprovalBoundaryTests` para o contrato desejado:
   turno textual, agente opaco, escrita pendente e gate com contexto confiável.
2. Excluir `HarnessConfirmationLifecycleTests` e
   `ApprovalCoordinatorDurableLifecycleTests`, pois exercitam somente o
   coordenador removido.
3. Remover a referência AI do projeto de integração Foundation.
4. Aplicar a menor implementação que satisfaça os testes preservados.
5. Executar `dotnet restore Vinx.Erp.sln` para atualizar somente lockfiles
   afetados.

## Tarefa 3 — alinhar documentação com o runtime real

**Arquivos:**

- modificar `docs/modules/ai.md`;
- modificar
  `docs/verification/issue-127-package-first-harness-test-cases.md`;
- modificar `docs/verification/issue-127-vinx-policy-test-cases.md`;
- marcar `docs/superpowers/specs/2026-08-14-vinx-policy-approval.md` como
  parcialmente substituído.

**Passos:**

1. Remover alegações de host, retomada, replay local, timers e shutdown.
2. Separar claramente a primitiva durável mantida do runtime não entregue.
3. Manter apenas cenários públicos que apontem para testes existentes.

## Tarefa 4 — verificar e revisar criticamente

**Comandos focados:**

```bash
dotnet test tests/public/Vinx.Erp.AI.UnitTests --no-restore --nologo -v q
dotnet test tests/public/Vinx.Erp.Foundation.UnitTests --no-restore --nologo -v q
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests --no-restore --filter FullyQualifiedName~ApprovalServicesIntegrationTests --blame-hang-timeout 60s --blame-hang-dump-type none -v q
dotnet test tests/public/Vinx.Erp.ArchitectureTests --no-restore --filter Category!=SupabaseStorage -v q
git diff --check
```

**Gate público completo:**

```bash
./scripts/verify-public.sh
```

**Revisão:**

1. Pedir revisão independente do diff final.
2. Confirmar que nenhuma API pública permite entregar aprovação ao agente.
3. Confirmar que nenhum host passa a executar ferramentas.
4. Confirmar que Foundation conserva migrações, autorização, auditoria e
   testes próprios.
5. Corrigir achados reais; rejeitar sugestões que recriem abstração sem uso.

## Tarefa 5 — publicar um único PR

1. Registrar no corpo do PR a estimativa, o grande saldo negativo de linhas e
   a regressão de compilação observada na base.
2. Separar no corpo: entregue agora, invariantes preservados e trabalho futuro.
3. Criar commit intencional, enviar `codex/backend-simplification` e abrir um
   draft PR contra `main`.
