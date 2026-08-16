# Registro global de operador — plano curto #209

**Objetivo:** criar o primeiro `platform.operator` por comando de deployment
idempotente, sem conceder acesso implícito a empresas.

**Limite:** 16–18 arquivos e aproximadamente 500–650 linhas manuais. Esta é
a camada entre #110 e #208 na stack. Não inclui tenant, convite, funcionário,
UI, entrega externa, MFA/TOTP completo ou idempotência global genérica.

## Decisões aplicadas

- `platform.operator` é um registro singleton global, separado de
  membership, role e capability de empresa.
- Mesmo `issuer` + `subject` confirmado pelo Supabase Admin é replay seguro;
  outro subject depois do bootstrap é conflito sem criação parcial.
- O comando cria a identidade interna somente após confirmar `id`, audience,
  confirmação e e-mail no provider. Nome de exibição vem de configuração de
  deployment não secreta; nunca de `user_metadata` para autorização.
- O banco faz a mutação e a auditoria global na mesma transação por funções
  `SECURITY DEFINER` com owner `NOLOGIN/NOBYPASSRLS`. Runtime não recebe DML
  direto nem `EXECUTE` da função de bootstrap.
- A policy HTTP reconsulta o registro no banco; indisponibilidade vira `503`
  seguro e negação verdadeira é `403`. O bootstrap existente só expõe o
  booleano de descoberta, nunca decide autorização no cliente.

## Tarefas

1. Escrever RED para DDL/grants/RLS/funções e para o comportamento de replay,
   conflito, rollback e isolamento. Gerar a migration somente depois do RED.
2. Criar a migration SQL e o modelo mínimo `PlatformOperator`, incluindo
   owner dedicado, função de leitura do ator e função de bootstrap atômico.
3. Criar o cliente REST estreito do Supabase Admin e o console de deployment;
   validar configuração sem imprimir segredo, subject, e-mail ou resposta do
   provider.
4. Adicionar serviço/policy global e estender `GET /foundation/bootstrap` com
   estado de plataforma. Cobrir `403`, `503`, ausência de membership e não
   enumeração de tenant.
5. Atualizar manifesto de verificação, documentação Foundation e snapshot;
   revisão Sol de segurança, congelar código, executar o gate público uma vez
   antes do push e publicar como draft na stack.

## Verificação mínima

- migration/privilege tests em PostgreSQL real;
- testes focados de client/provider, serviço e endpoint;
- `./scripts/verify-public.sh` somente no estado congelado imediatamente
  antes de `gh stack submit`.
