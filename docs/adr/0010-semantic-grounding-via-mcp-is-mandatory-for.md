# ADR-0010: Semantic grounding via MCP is mandatory for agents

- **Status:** Accepted (founding decision; amend only via a superseding ADR)
- **Date:** 2026-11-16
- **Deciders:** project owner, Architect persona

## Context

Agents that grep F# introduce subtle type errors; compiler-semantic tools measurably help.

## Decision

FSharpened.Semantic wraps FsLangMCP/SageFs and exposes a library plus stdio MCP server. Agents must use it for symbol resolution.

## Consequences

CI can reject agent PRs that use text search for symbols when the server is available.
