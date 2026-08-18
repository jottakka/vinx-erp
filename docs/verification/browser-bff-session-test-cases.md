# Casos BDD — issue 219, sessão Browser BFF

## Escopo

As rotas de mesma origem são `GET`, `POST` e `DELETE` em
`/foundation/identity/browser-session`. O browser recebe somente cookie de
prova; token de acesso, refresh token, e-mail e chamada direta ao Supabase não
fazem parte do contrato.

| Classe BDD | Dado / quando / então | Evidência pública | Resultado esperado |
|---|---|---|---|
| login válido | Dadas credenciais aceitas pelo provedor, quando `POST` com `application/json` (`email` e `password`) recebe `X-CSRF-TOKEN` válido, então cria prova local e bootstrap autentica | `BrowserBffSessionFlowTests.Password_grant_creates_a_proof_only_browser_cookie_that_authenticates_bootstrap` | `204`; cookie sem token, refresh ou e-mail |
| limite do corpo de login | Dado JSON maior que 8 KiB, inclusive em transferência chunked sem tamanho declarado, quando `POST` recebe CSRF válido, então não chama o provedor nem emite cookie | `BrowserBffSessionFlowTests.Oversized_login_body_is_rejected_without_calling_provider_or_setting_a_browser_session_cookie`; `Chunked_oversized_login_body_is_rejected_without_calling_provider_or_setting_a_browser_session_cookie` | `400 {"code":"invalid_request"}`; sem cookie |
| erro de login | Dado provedor que rejeita, falha ou identidade não provisionada, quando login é chamado, então não cria cookie | `BrowserBffSessionFlowTests.Invalid_provider_credentials_return_empty_401_without_a_browser_session_cookie`; `Provider_failure_returns_safe_503_without_a_browser_session_cookie`; `Unprovisioned_provider_identity_returns_empty_401_without_a_browser_session_cookie` | `401` vazio ou `503` seguro; sem cookie |
| selector | Dado cookie válido e `Authorization` malformado, quando chama operação protegida, então bearer é selecionado sem fallback | `BrowserBffSessionFlowTests.Malformed_authorization_header_never_falls_back_to_a_valid_browser_cookie` | `401` vazio |
| CSRF e logout | Dado cookie válido, quando `DELETE` não traz `X-CSRF-TOKEN`, então falha sem revogar ou remover cookie; com CSRF válido, revoga sessões próprias e remove cookie | `BrowserBffSessionFlowTests.Cookie_only_sign_out_requires_antiforgery_then_revokes_all_sessions_and_clears_the_cookie`; `BrowserBffSessionFlowTests.Missing_antiforgery_on_sign_out_keeps_the_browser_session_usable` | `400` sem CSRF; `204` e um cookie expirado com CSRF |
| gate indisponível | Dado cookie válido e CSRF válido, quando resolução no gate falha antes do handler, então mantém `503` seguro e remove cookie local | `BrowserBffSessionFlowTests.Sign_out_returns_safe_503_and_clears_a_valid_cookie_when_session_resolution_is_unavailable` | `503 {"code":"identity_gate_unavailable"}` e um cookie expirado |
| revogação indisponível | Dado logout que chega ao handler, quando revogação global falha, então remove cookie uma vez e não altera sessões | `BrowserBffSessionFlowTests.Sign_out_returns_safe_503_and_clears_the_cookie_when_global_revocation_is_unavailable` | `503` seguro e um cookie expirado |
| fronteiras de cookie | Dado pedido anônimo, bearer válido, `Authorization` inválido ou CSRF inválido, quando chama logout, então não remove cookie; bearer válido não revoga sessão e recebe `403` vazio | `BrowserBffAuthorizationTests.Anonymous_browser_session_delete_returns_empty_401_without_revocation_side_effect`; `BrowserBffAuthorizationTests.Bearer_and_browser_cookie_on_cookie_only_sign_out_returns_empty_403_without_clear_or_revocation`; `BrowserBffCookieSecurityTests.Cookie_authenticated_mutation_requires_csrf_while_bearer_mutation_does_not`; `BrowserBffSessionFlowTests.Malformed_authorization_header_never_falls_back_to_a_valid_browser_cookie` | sem bypass ou remoção indevida; `403` vazio para bearer válido |
| cache e limite | Dada rota BFF, quando responde, então não permite cache; 101 emissões de CSRF não consomem limite de login; alias de login aceito compartilha o limite global | `BrowserBffCookieSecurityTests.Sign_out_clears_the_browser_cookie_with_private_no_store`; `BrowserBffEndpointTests.Browser_antiforgery_is_not_rate_limited_by_the_login_limit`; `BrowserBffEndpointTests.Browser_session_alias_shares_the_global_login_limit` | `Cache-Control: private, no-store`; todos os `GET` retornam `200`; `429` após 100 logins globais |

## Limites mantidos

#44 continua sem JIT por e-mail, MFA, recuperação e fluxos adicionais do
provedor. #79 continua sem listagem ou revogação individual de sessões. O
logout BFF revoga somente sessões próprias e não executa logout ou banimento no
provedor.
