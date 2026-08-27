# Issue #346 — diretório explícito de aprovadores Pagaí

| Cenário | Teste público | Intenção | Situação |
| --- | --- | --- | --- |
| Capability e fronteira SQL | `Capability_and_directory_have_the_canonical_least_privilege_contract` | Fixa UUID, owner sem login/bypass, `search_path`, RLS forçado e `vinx_worker` como único executor de runtime. | Automatizado |
| Aprovação explícita e tenant | `Worker_lists_only_explicit_non_requester_approvers_from_the_requested_company` | Retorna somente grants `company` vivos da empresa pedida, sem solicitante nem usuário de outra empresa. | Automatizado |
| Parâmetros inválidos | `Directory_rejects_null_or_empty_identifiers` | Rejeita empresa ausente e usuário excluído vazio com `22023`. | Automatizado |
| Administrador padrão | `Default_admin_is_not_an_approver_without_an_explicit_financial_grant` | Prova que `ensure_company_default_roles` não concede autorização financeira implícita. | Automatizado |
| Remoções e estado | `Directory_excludes_revoked_memberships_assignments_roles_and_grants` | Exclui membership, assignment, role ou grant removido logicamente. | Automatizado |
| Duplicidade e concorrência | `Duplicate_grants_are_deduplicated_across_concurrent_directory_reads` | Deduplica usuário com múltiplos grants, mantém leituras concorrentes estáveis e respeita revogação do último grant. | Automatizado |
| Down seguro | `Downgrade_refuses_references_then_removes_only_the_directory_contract` | Recusa referência explícita, preserva a migration na falha e remove só a função quando livre. | Automatizado |
| Capability preexistente | `Preexisting_canonical_capability_survives_upgrade_and_successful_downgrade` | Conserva UUID, nome, descrição e criação de uma linha canônica anterior à migration. | Automatizado |

Fora desta fatia: endpoint, escolha de política, fluxo de aprovação e grants automáticos.
