# Casos BDD — issue 221, correlação de resposta API

| Classe BDD | Dado / quando / então | Evidência pública | Resultado esperado |
|---|---|---|---|
| sucesso | Dada uma chamada saudável a `/health`, quando o ApiHost responde, então devolve correlação criada no servidor | `RequestCorrelationHeaderTests.Successful_response_contains_a_server_created_correlation_id` | `200` e um `X-Correlation-ID` UUID válido |
| validação | Dado header de correlação forjado, quando chega ao ApiHost, então ele não é refletido | `RequestCorrelationHeaderTests.Forged_incoming_correlation_id_is_ignored` | UUID novo e diferente, sem confiar no cliente |
| acesso | Dada chamada não autenticada, quando o gate devolve `401`, então a referência permanece segura | `RequestCorrelationHeaderTests.Unauthorized_response_contains_a_server_created_correlation_id` | `401` sem corpo e header opaco |
| estado/exceção | Dado gate indisponível ou exceção tratada, quando a resposta segura é emitida, então a correlação permanece e detalhes não vazam | `RequestCorrelationHeaderTests.Identity_gate_unavailable_response_contains_a_server_created_correlation_id`; `Exception_response_contains_a_server_created_correlation_id_without_sensitive_details` | `503` ou `500` seguro e header UUID |
| concorrência | Dadas requisições simultâneas, quando todas concluem, então cada resposta tem referência isolada | `RequestCorrelationHeaderTests.Concurrent_responses_contain_distinct_server_created_correlation_ids` | UUID não vazio e distinto por requisição |
| contrato | Dado OpenAPI de Development, quando descreve bootstrap, então anuncia correlação somente como header de resposta | `RequestCorrelationHeaderTests.Bootstrap_openapi_declares_correlation_id_as_a_response_header` | header `string` em todos os outcomes publicados, sem parâmetro de entrada |
