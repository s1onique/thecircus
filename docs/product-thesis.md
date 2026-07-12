# Leamas and The Circus

Leamas is a repository-scale engineering agent.

The Circus is its team-scale coordination, evidence, and governance platform.

A local Leamas instance executes work close to a repository and its native toolchain. The Circus will provide the shared organizational layer across development teams and repositories.

The intended capability chain is:

```text
engineering intention
    → contract
    → repository execution
    → verification evidence
    → review
    → organizational learning
```

The first implementation phase is observational. Leamas instances will report executions and evidence to The Circus, allowing teams to inspect work across repositories without granting The Circus remote shell access.

Future areas may include:

- Leamas fleet visibility
- engineering initiatives spanning repositories
- versioned doctrine distribution
- execution and evidence history
- review and acceptance workflows
- doctrine-effectiveness analysis
- controlled rollout and compatibility reporting

The Circus does not initially replace:

- source control
- CI
- local development tools
- artifact storage
- incident management
- remote orchestration

Its first role is to make distributed engineering work centrally visible, reviewable, and measurable.
