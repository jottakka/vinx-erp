# Casos de verificação — provisionamento inicial de organização

| Área | Cenário | Resultado exigido |
|---|---|---|
| Policy | Ator sem a identidade externa singleton de operador chama a rota. | `403 platform_operator_required`, sem revelar MFA nem criar estado. |
| Policy | Operador exato com assurance `aal1` chama a rota. | `403 mfa_assurance_required`, auditoria global de negação e nenhuma mutação. |
| SQL | Operador `aal2` chama a função com request novo. | Cria grupo, empresa, usuário local, membership, role `owner`, quatro grants, assignment, convite pendente, recibo e auditoria na mesma transação. |
| Idempotência | Mesmo ator repete request e hash canônico. | Um resultado `created`, os demais `replay`, com os mesmos IDs opacos e sem duplicar o grafo. |
| Idempotência | Mesmo ator e request usam hash diferente. | `idempotency_conflict`, sem mutação parcial. |
| Isolamento | Runtime tenta ler tabelas técnicas ou sombrear relações por tabela temporária. | Não há privilégio direto; a função usa relações qualificadas e mantém o resultado correto. |
| HTTP | Corpo malformado, campo desconhecido ou duplicado é enviado. | `400 {"code":"invalid_request"}` antes do serviço, sem alterar a precedência da autorização. |

Evidência automatizada: `PlatformOrganizationProvisioningFunctionTests`,
`PlatformOrganizationEndpointTests` e os testes focados de policy de operador.
