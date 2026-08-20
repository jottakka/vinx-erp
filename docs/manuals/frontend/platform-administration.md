# Administração da plataforma

A rota protegida `/administracao` permite a um operador de plataforma criar a
organização inicial: grupo, empresa e dono, com convite pendente de entrega. O
conteúdo administrativo só é habilitado quando o snapshot de sessão traz
`platform.isPlatformOperator === true`; isso é um filtro de UX, não uma decisão
de autorização. O endpoint `POST /foundation/platform/organizations`, a política
de operador, a exigência de AAL2 e o RLS continuam como a autoridade — refresh,
revogação ou troca de identidade removem o conteúdo na próxima renderização.

O navegador usa apenas caminhos BFF same-origin (`credentials`, `mode`
`same-origin`, `redirect: "error"`, `cache: "no-store"`), sem token, provider
ou armazenamento local. Para cada POST, obtém um antiforgery transitório em
memória e o envia uma única vez como `X-CSRF-TOKEN`. Não existe retry
automático nem inserção otimista: cada tentativa nova usa um `requestId`
fresco. Durante a submissão, os campos ficam travados e um status acessível
(`role="status"` + `aria-busy`) anuncia "Criando organização…". Falhas
ocorridas antes do disparo do comando (por exemplo, no GET de antiforgery)
são definitivas e apenas exibem erro genérico. Somente falhas sinalizadas
internamente com `mayHaveCommitted` — rede, `5xx` ou abort após o disparo, ou
sucesso `200/201` com corpo ilegível — travam o formulário e oferecem um
retry explícito que reusa o mesmo `requestId` e o mesmo payload, preservando a
idempotência do servidor.

A negação `403 {"code":"mfa_assurance_required"}` entra em estado terminal: a
página orienta sair e entrar novamente com segundo fator, sem liberar nova
submissão na mesma sessão. Nenhuma mutação de negócio foi executada; somente a
auditoria de negação pode ter sido persistida. `401` e
`platform_operator_required` revalidam o bootstrap e removem o conteúdo se o
gate cair.

O recibo de sucesso é allow-listed: apenas "Organização criada." (ou "já havia
sido criada." no replay) e o estado do convite
(`pending_delivery`/`pending_acceptance`). Identificadores opacos, e-mail
persistido, token, payload do provider e detalhes de exceção não são
renderizados; mensagens de conflito (`409`) são genéricas para não enumerar o
que já existe. A operação captura a identidade do operador: resposta ou retry
de outra identidade são descartados.

Ficam para uma fatia posterior: link de navegação condicional para operadores,
step-up MFA dentro da tela, aceite de convite, lista global de empresas,
edição de papéis, múltiplas filiais, organograma e auto-cadastro. A tela
`/pessoas`, Paguai, Atendimento, Kanban, agente e WhatsApp não são alterados.
