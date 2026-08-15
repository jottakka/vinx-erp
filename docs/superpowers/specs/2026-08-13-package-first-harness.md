# Harness package-first do Agent Framework — substituído

## Status

Este desenho foi integralmente substituído em 2026-08-15 por
`docs/superpowers/specs/2026-08-15-backend-first-simplification.md`.

A prova package-first não possuía host nem consumidor de produção. A PR #207
removeu o projeto `Vinx.Erp.AI`, suas sondas e testes, além das dependências
diretas do Agent Framework. As aprovações duráveis da Foundation permanecem.

## Decisão futura

Quando #135 e #138 tiverem provider e API reais, a implementação deve começar
pelo Microsoft Agent Framework, `IChatClient` e SDK oficial do provider. A
adoção ocorrerá na mesma fatia vertical que registra e usa o runtime; não será
reintroduzida como módulo demonstrativo separado.
