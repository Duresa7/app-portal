# List queries are handed to the stores, not turned into SQL

The six administration list pages share one module that describes which slice of a list is wanted, how it is sorted, and what it is filtered by (M1-12). That module hands the description to the store that owns the table and never composes SQL itself.

The reason is that the stores are not uniform, and should not be. A handful of their methods hold guarantees a caller cannot see: the stale-write guard in `InstallStore.Upsert`, the coordinated statements in `DeviceStore.RemoveById` that keep a retired device's history, the pending-request quota in `AppRequestStore.CreateCore`. A module that generated SQL would start with the easy list queries and grow until those guarantees were routed around.

This decides who composes a list query. It deliberately leaves open whether `Data/Database` should later grow shared query machinery so the stores stop hand-writing parameter binding and row mapping seven times over. That is a separate question about how a store is written, and it has not been worked through.
