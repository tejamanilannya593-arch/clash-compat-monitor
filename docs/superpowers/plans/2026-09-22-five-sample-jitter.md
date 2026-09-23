# Five-sample robust node quality implementation plan

**Goal:** Reduce automatic selector churn and false “better node” decisions by requiring a meaningful recent sample window and evaluating latency, tail latency, jitter, and observed failure rate together.

## Batch 1: Characterization tests

- Extend `tests/Tests.cs` with failing cases for a five-sample minimum, a ten-sample recent window, P75/P90 statistics, spike-sensitive jitter, and outcome-based failure-rate ranking.
- Run the focused test executable and record the expected failures before production edits.

## Batch 2: Quality statistics and decision wiring

- Add one shared recent-window statistics implementation (minimum 5, maximum 10 samples).
- Make current-node optimization and candidate preference consume the shared statistics.
- Store only the latest ten response samples; derive median response and robust jitter from that window.
- Use actual successful/total outcomes in node ranking when outcome samples exist.
- Update automatic monitoring to request the full ten-sample window.
- Run focused tests, the complete test suite, and the build.

## Batch 3: Immutable preview.10 release

- Bump all version and release references to `0.7.0-preview.10` and add release notes.
- Package the release and generate SHA-256 files.
- Replace the installed preview.9 instance, confirm one watchdog/process and startup target, then observe at least two automatic cycles.
- Confirm configuration/subscription files are unchanged, copy the folder/ZIP/hash to the product directory, commit, push, and update the existing pull request.
