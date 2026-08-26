# Permissões de membros

A rota protegida `/permissoes` permite a administradores gerenciar papéis e
convites dos membros de uma empresa selecionada explicitamente. A tela só
oferece empresas presentes no bootstrap com a capability
`foundation.roles.assign` no escopo `Company`; isso é um filtro de UX, não uma
decisão de autorização. O endpoint, a função SQL e o RLS seguem como a
autoridade. Quem não tem a capability em nenhuma empresa vê apenas um aviso
informativo, sem nenhuma consulta ao servidor.

A tela lista membros com chips de papel — a remoção acontece no próprio chip —
e chip de convite (`Convite pendente`, `Falha na entrega`, `Entrega incerta`).
Um único diálogo por membro, "Gerenciar acesso", reúne os papéis atuais
removíveis, a adição de um papel do catálogo da empresa e a cópia dos papéis
de outro membro (substituição completa); convidar uma pessoa por nome e e-mail
usa o diálogo de convite. A empresa selecionada exibe o resumo de membros e
administradores, e a lista aceita busca por nome sem diferenciar maiúsculas ou
acentos. O convite cria a reserva do membro e o convite `pending_delivery`;
o aceite e o primeiro login chegam com a #211C. O navegador usa apenas os
caminhos BFF same-origin, sem token ou armazenamento local. Para cada POST,
obtém um antiforgery transitório em memória e o envia uma única vez. Não
existe retry automático nem inclusão otimista; o sucesso revalida as listas
de membros e papéis.

E-mail permanece somente em memória: no formulário de convite e, após
resultado ambíguo, na tentativa preservada para retry explícito com o mesmo
`requestId` e o mesmo payload, descartada no cancelamento, na troca de
empresa ou na supersessão de sessão; identificadores opacos, capabilities,
respostas brutas e corpos de requisição não são expostos. A remoção que
deixaria a empresa sem administrador ativo é recusada com "A empresa precisa
de ao menos um administrador ativo."; e-mail já cadastrado mostra "Este
e-mail já possui cadastro." e não revela o registro existente. Em `401` ou
`403`, a seleção da empresa é removida e o bootstrap é revalidado antes de a
interface continuar.

Ficam para fatias posteriores: aceite do convite e primeiro login do
convidado (#211C), edição e criação de papéis, notificação por WhatsApp ou
Telegram (#305/#306) e re-convite de entrega falha ou incerta — até a
reconciliação, o e-mail reservado retorna conflito. Paguai, Atendimento e
Kanban não são alterados por esta rota.
