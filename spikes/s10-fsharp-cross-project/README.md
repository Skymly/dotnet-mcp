# Spike S10 — F# cross-project symbol search under FCS

Time-boxed verification for issue #522: in FCS, how do we find uses (references / callers /
implementations / type hierarchy / rename) of an F# symbol in *other* F# projects that
reference the defining project — in-memory project references (route A) vs `-r:dep.dll`
(route B) — and at what cost?

See [CONCLUSIONS.md](CONCLUSIONS.md).

Run: `dotnet test spikes/s10-fsharp-cross-project/src -c Release`
