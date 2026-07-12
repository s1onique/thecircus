# The Circus

The Circus is the team-scale coordination, evidence, and governance
platform for Leamas.

Leamas operates close to an individual repository. The Circus provides
the shared layer for development teams working across repositories,
engineering initiatives, doctrine versions, executions, and evidence.

## Status

This project is **experimental**.

Application implementation has not started. This repository establishes
the Factory substrate from Leamas.

## Factory Doctrine

Factory doctrine is compiled from Leamas using the `fsharp-elm-service-v1`
profile from the `factory-core-v1` pack.

Generated Factory files must not be edited manually:

- `.factory/doctrine.lock.json`
- `.factory/generated/*`
- `docs/factory/README.md`

## Commands

- `make factorize` — run read-only Factory verification
- `make gate` — run the repository's native quality gate

## Documentation

- [docs/product-thesis.md](docs/product-thesis.md) — product thesis and design intent
- [.factory/generated/doctrine-inventory.md](.factory/generated/doctrine-inventory.md) — enabled Factory doctrines
