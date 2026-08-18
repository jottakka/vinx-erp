# Diretório de pessoas

A rota protegida `/pessoas` permite consultar o diretório e reservar um
funcionário local para uma empresa selecionada explicitamente. A tela só mostra
empresas presentes no bootstrap com a capability
`foundation.employees.create` no escopo `Company`; isso é um filtro de UX, não
uma decisão de autorização. O endpoint, o serviço, a função SQL e o RLS seguem
como a autoridade.

Nenhuma consulta ou reserva é iniciada antes da seleção. O navegador usa apenas
os caminhos BFF same-origin permitidos, sem token ou armazenamento local. Para
cada POST, obtém um antiforgery transitório em memória e o envia uma única vez.
Não existe retry automático nem inclusão otimista. Depois de resultado ambíguo,
o usuário pode tentar novamente de forma explícita com o mesmo `requestId` e o
mesmo payload, preservando a idempotência do servidor.

A lista exibe somente nomes. E-mail permanece somente em memória: no
formulário e, após resultado ambíguo, na tentativa preservada para retry
explícito com o mesmo `requestId`, descartada na reconciliação ou na
supersessão de sessão; identificadores opacos, papel, convite, provider e
respostas brutas não são expostos. Em `401` ou `403`, a consulta da empresa é removida e o bootstrap é
revalidado antes de a interface continuar.

Ficam para uma fatia posterior: provisionamento de organização, MFA/AAL2,
convites, outbox e entrega por provider, aceite e edição de papéis. Paguai,
Atendimento e Kanban não são alterados por esta rota.
