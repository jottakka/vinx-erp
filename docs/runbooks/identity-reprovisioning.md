# Recuperação offline de identidade

Use `reprovision-identity` somente para a recuperação aprovada da mesma
pessoa. O vínculo interno é escolhido pelo ID de usuário e pelo ID de
identidade previamente conferidos; e-mail confirmado é apenas uma guarda de
igualdade e nunca um critério de vínculo.

## Pré-condições e configuração

O ambiente seguro fornece `FOUNDATION_MIGRATOR_CONNECTION`,
`SUPABASE_AUTH_ISSUER`, `SUPABASE_AUTH_AUDIENCE`, a credencial Admin Auth e:

- `REPROVISION_IDENTITY_USER_ID`;
- `REPROVISION_IDENTITY_EXPECTED_EXTERNAL_IDENTITY_ID`;
- `REPROVISION_IDENTITY_EXPECTED_OLD_SUBJECT`;
- `REPROVISION_IDENTITY_NEW_SUBJECT`.

Os IDs devem ser UUIDs não vazios; os subjects são exatos, sem espaços nas
bordas. O preflight exige `NotFound` para o subject antigo e usuário novo com
ID, audiência, e-mail confirmado, identidade compatível, sem banimento e não
anônimo. Falha em qualquer etapa não chama a mutação SQL.

## Execução e pós-checagem

```bash
dotnet run --project src/Tools/Vinx.Erp.PlatformBootstrap -- reprovision-identity
```

O resultado é somente um código estável: `reprovisioned`,
`already_configured`, `provider_not_deleted`, `provider_rejected`,
`email_mismatch`, `tuple_mismatch`, `unavailable` ou `configuration_invalid`.
Após sucesso, valide novo login e sessão. Negação ou indisponibilidade exige
escalonamento pelo canal seguro; não use SQL direto, e-mail para religar conta,
identificadores em tickets, payload do provedor ou segredo em diagnóstico.
