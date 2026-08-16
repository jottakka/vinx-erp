# Plano: bootstrap autenticado do frontend (#90)

## Objetivo

Consumir `GET /foundation/bootstrap` com contrato tipado e manter sua projeção
somente em memória, sem transformar capabilities, flags ou operador de
plataforma em autorização no navegador.

## Limites

- Esta fatia entrega cliente Bootstrap, estado/atualização segura em memória,
  invalidação do cache autenticado e a projeção de todas as empresas.
- Login, logout BFF no fluxo de release, substituição do mock padrão e jornada
  Playwright com sessão real ficam em #117.
- Contagem não lida não existe no contrato atual e fica fora desta fatia.

## Etapas

1. Criar testes RED para contrato, erros seguros e transporte de mesma origem.
2. Preservar no snapshot IDs de membership e empresa, scopes de capability,
   flags globais e projeção de operador; adaptar as fixtures mockadas ao mesmo
   formato.
3. Revalidar o snapshot em geração monotônica, abortar leitura substituída e
   limpar cache autenticado antes de publicar uma nova autorização.
4. Atualizar a visão de todas as empresas, documentação e matriz BDD; testar,
   revisar e publicar a PR empilhada.
