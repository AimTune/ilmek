---
id: supersteps
title: Supersteps (BSP)
sidebar_label: Supersteps
sidebar_position: 3
---

# Supersteps

*Normative: [MODEL.md §4](/reference/spec).*

Execution runs as a loop of **supersteps**, in the Bulk Synchronous Parallel
style. Each superstep plans the next tasks, runs them concurrently, folds their
updates, and checkpoints — atomically.

```
loop:
  1. PLAN       — resolve edges from the last checkpoint → the set of next tasks
  2. RUN        — execute all tasks of this superstep CONCURRENTLY
  3. REDUCE     — fold every update into channels, in task order
  4. CHECKPOINT — persist { channels, next tasks } atomically
  5. halt if:   no next tasks · an interrupt is pending · recursion limit hit
```

## Guarantees

**Isolation.** Tasks within a superstep each see the state as of the *previous*
checkpoint. A task never observes a sibling's update — which is why the
[conflict rule](/model/state-and-channels#the-conflict-rule) folds concurrent
writes in deterministic task order.

**Atomicity.** A superstep is all-or-nothing: either every update is reduced and
checkpointed, or none is. A crash mid-superstep resumes the *whole* superstep —
surviving tasks fast-forward through their [journals](/model/journal), so no
completed step re-runs.

**Bounded.** A recursion limit (default **25**) caps the superstep count.
Exceeding it raises `RecursionLimitError` — a guard against a router that loops
forever.

## Why BSP

The BSP discipline is what makes a run *resumable* and *deterministic* at once.
Because state only advances at checkpoint boundaries, a resume is just "re-run the
superstep that was in flight" — and because the journal already holds every
completed step, that re-run is free of double-effects. The
[journal](/model/journal) and [interrupts](/model/interrupts) both build directly
on this boundary.
