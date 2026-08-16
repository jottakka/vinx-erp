# Bootstrap autenticado do frontend

## Objetivo

Depois que a sessão já foi admitida pelo servidor, o frontend consulta
`GET /foundation/bootstrap` para montar a visão inicial. A resposta fica
somente em memória: não é salva em localStorage, sessão do navegador, cache
offline ou banco local.

## Conteúdo recebido

O snapshot contém a pessoa atual, cada membership ativa com `membershipId` e
`companyId`, capabilities com seus scopes, flags globais e a projeção de
operador de plataforma. A página **Início** começa em **Todas as empresas
autorizadas**; isso é uma união de leitura, nunca uma empresa fictícia nem um
contexto válido para comando.

Cada chamada de domínio futura informa a empresa explicitamente. Capabilities,
flags e a projeção de operador orientam somente a interface: o servidor decide
autorização novamente em toda operação.

## Atualização e indisponibilidade

Ao revalidar o bootstrap, a interface remove o snapshot e limpa queries
autenticadas antes de mostrar o resultado novo. Uma membership revogada some
no snapshot seguinte sem remover as demais empresas autorizadas. Resposta
antiga ou abortada não pode restaurar acesso.

Uma resposta `401` deixa a pessoa anônima. Falha transitória, rate limit ou
contrato inválido não finge logout: mantém a rota protegida bloqueada e oferece
uma tentativa segura, sem exibir detalhes do servidor. Reconexão apenas dispara
nova leitura; nunca repete uma mutação.

## Flags

O contrato atual publica apenas flags globais booleanas. Flag ausente vale
`false` e nenhuma flag concede membership, capability ou acesso. O registro
durável de dono, motivo e data de remoção fica na configuração Foundation que
valida essas propriedades no servidor; não há flag de produto publicada nesta
fatia para duplicar no browser.

## Limite da entrega

Esta fatia estabiliza o cliente e o estado do bootstrap. A entrada e saída BFF
no caminho de release, o abandono do mock padrão e a jornada autenticada no
navegador pertencem a #117.
