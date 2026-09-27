# Flex Companion v0.6.0.1

- Fixes the compound analogue meter render loop introduced in v0.6.0.
- The cached WPF `DrawingGroup` is now frozen only after its `DrawingContext` has been closed.
- Adds a one-dialog fail-safe for unexpected UI exceptions so a future render fault cannot create an endless warning loop.
- Preserves all four meter modes, source-paced updates, selective telemetry subscriptions and existing-settings migration.
