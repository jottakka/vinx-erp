# Contrato de correlação de resposta — plano curto #221

**Objetivo:** publicar `X-Correlation-ID` gerado pelo ApiHost para que o
cliente web de #16 trate falhas de forma segura, sem aceitar valor do browser
nem expor detalhe interno.

**Estimativa:** 7 arquivos contados, aproximadamente 180–280 linhas manuais;
um contrato HTTP único, sem migration ou dependência nova.

1. Escrever RED para cabeçalho em sucesso, negação, exceção tratada e pedidos
   concorrentes; cobrir valor forjado ignorado.
2. Registrar o header no middleware de correlação antes do handler e declará-lo
   no OpenAPI sem mudar corpos ou status existentes.
3. Atualizar módulo, política OpenAPI e manifesto BDD; fazer revisão Sol,
   congelar, rodar o gate público uma vez e publicar draft empilhada sobre #220.

**Adiamentos:** cliente TypeScript, proxy Vite e consumo de bootstrap ficam na
PR frontend seguinte de #16; autenticação, sessão e UI continuam em #90/#117.
