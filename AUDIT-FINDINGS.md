# DotNetMcp Read-Only Security & Correctness Audit

**Repository:** github.com/Skymly/dotnet-mcp  
**Nature:** Investigation only — no production/runtime code was modified in this pass  
**Scope:** MCP tool surface, trusted-root / Workspace Edit restriction, path handling, process execution, parsers, correctness, design  
**Date:** 2026-09-01  

Identifiers below match the on-disk tree under `src/DotNetMcp.*` and `tests/DotNetMcp.Tests`.

---

## Architecture overview

### How tools are exposed

- The stdio MCP host is assembled in `ServerHost` (DI + assembly tool discovery).
- The tool surface is locked by `ToolSurfaceGuardTests` (exact allowlist of **31** tools):

- `diagnostics_apply_fix` · `diagnostics_list_fixes` · `diagnostics_preview_fix` · `project_diagnostics` · `project_list_dynamic_invocations` · `project_list_generated_sources` · `project_list_generator_diagnostics` · `project_list_generators` · `symbol_apply_refactoring` · `symbol_apply_rename` · `symbol_attribution` · `symbol_find_callers` · `symbol_find_implementations` · `symbol_find_references` · `symbol_goto_definition` · `symbol_list_refactorings` · `symbol_members` · `symbol_preview_refactoring` · `symbol_preview_rename` · `symbol_resolve` · `symbol_summary` · `symbol_type_hierarchy` · `workspace_check_drift` · `workspace_list_projects` · `workspace_open` · `workspace_status` · `xaml_diagnostics` · `xaml_list_xmlns` · `xaml_resolve_binding` · `xaml_resolve_class` · `xaml_resolve_name`


- Read tools go through `McpToolEnvelope` (ready-session gate + JSON envelope). Writes are only kind-tagged Workspace Edit flows (Rename / Diagnostic fix / Refactoring): preview then apply.
- `ToolSurfaceGuardTests` also forbids generic write / shell / HTTP / network name+description fragments. No product `Process.Start` / `HttpClient` / `WebRequest` under `src/`.

### How workspace restriction is enforced

| Boundary | Mechanism |
|----------|-----------|
| Startup | `TrustedRoots.FromStartup`: `--roots` / `DOTNET_MCP_TRUSTED_ROOTS`; **default = process CWD** |
| Normalize | `PathPolicy.Normalize` = `GetFullPath` + longest-existing-prefix `ResolveLinkTarget` + `IsUnderRoot` prefix check (case-insensitive on Windows) |
| Call sites | `workspace_open` entry path; all `xaml_*` path args; `WorkspaceEdit` preview/apply document paths |
| Write path | `WorkspaceEdit.Apply` re-checks TrustedRoots → `IWorkspaceEditWriter.WriteDeclaredPaths` (`WorkspaceHost`) → `File.WriteAllText` (**writer does not re-run PathPolicy**) |
| Not covered | Projects/documents resolved from `.slnf` / MSBuild graph; F# disk walk (`CaptureFSharp` / `ReadFSharpDocuments`); diagnostics `filePath` locator; loaded analyzer assembly paths |

### Inherent documented hazard

ADR-0004 / `workspace_open` description: **open means execute** (MSBuild evaluation + project-referenced analyzers/source generators). Trusted roots cannot remove this; they only constrain the entry path and require operators not to open untrusted trees.

---

## Top 10 (priority)

1. **Parent symlink/junction leaves PathPolicy lexical-under-root while realpath is outside** (High) — enables read and Workspace Edit write outside the root.  
2. **`.slnf` / project graph load never re-checks TrustedRoots** (High) — in-root filter can pull out-of-root projects (execute + read).  
3. **Default trusted root = CWD** (High) — launching from `$HOME` / `/` empties the sandbox.  
4. **Open-means-execute (inherent)** (Critical / accepted) — prompt-injected `workspace_open` runs build logic as the server user.  
5. **Apply TOCTOU + writer without final-path binding** (High) — swap a leaf/parent link after Contains, then `File.WriteAllText` follows it.  
6. **F# snapshot re-read from disk every ready session; walks follow dir symlinks** (High/Medium) — Epoch skew + out-of-root `.fs` ingestion.  
7. **`WriteDeclaredPaths` skips TrustedRoots (defense-in-depth)** (Medium).  
8. **Generator attribution content-fallback mis-bind** (Medium).  
9. **XAML resolve with `projectId: null` can bind the wrong project** (Medium).  
10. **Missing security-critical path tests** (Medium) — no parent-symlink/junction/UNC/`\\?\`/out-of-root `.slnf` coverage.

---

## Findings

### F-01 — Critical (inherent / documented)

**Title:** `workspace_open` executes MSBuild and analyzers/generators in-process  

**Location:** `WorkspaceTools.WorkspaceOpen`; `MsBuildSolutionLoader`; Core generator driver; ADR-0004 §2  

**Evidence:** Tool description states MSBuild evaluation and project-referenced analyzers/source generators run. Load uses `MSBuildWorkspace`; attribution re-runs the generator driver.  

**Impact:** Any caller that can invoke `workspace_open` on a crafted tree (including a prompt-injected MCP client) gets build-logic code execution as the server user. Trusted roots do not stop execution inside an accepted tree.  

**Recommended fix:** Cannot fully eliminate under Roslyn MSBuildWorkspace. Keep SECURITY copy; fail-closed / narrow defaults (F-03); refuse out-of-root graph nodes after load (F-02); optional explicit confirmation for new roots / future metadata-only mode if ever feasible.

---

### F-02 — High

**Title:** Trusted-root check is entry-path only; `.slnf` / MSBuild graph can leave the root  

**Location:** `SlnfParser.ResolveProjectPaths`; `MsBuildSolutionLoader.OpenSlnfAsync`; `WorkspaceTools.WorkspaceOpen`  

**Evidence:** `workspace_open` calls `TrustedRoots.Contains` only on the user-supplied path. `.slnf` resolution uses `Path.GetFullPath(Path.Combine(solutionDir, entry))` with **no** TrustedRoots re-check, then `OpenProjectAsync` on each path. Absolute entries or `../` escape the root. The same class of issue applies to MSBuild `ProjectReference` / imports when opening an in-root `.sln`/`.csproj`.  

**Impact:** Sandbox bypass for the “all paths under trusted root” promise: load+execute analyzers for out-of-root projects; symbol/diagnostic/generated-source tools can return those documents; watchers/drift may track them. Preview/Apply usually refuses **absolute** out-of-root write paths via `Contains`, but **read and execute** already leaked. Combined with F-01 this is the main trusted-root depth failure.  

**Recommended fix:** After resolving every project (ideally documents/analyzer assemblies too), require `TrustedRoots.Contains`; hard-fail absolute/`..` `.slnf` entries with a distinct policy error. Add seam tests for out-of-root `.slnf` projects and ProjectReferences.

---

### F-03 — High

**Title:** Default trusted root is process CWD  

**Location:** `TrustedRoots.FromStartup` (~lines 60–63)  

**Evidence:** When `--roots` and `DOTNET_MCP_TRUSTED_ROOTS` are empty, `collected.Add(Directory.GetCurrentDirectory())`.  

**Impact:** MCP hosts that start with CWD=`$HOME` or `/` make almost the entire readable tree openable; with F-01/F-02 the blast radius is huge under prompt injection.  

**Recommended fix:** Fail closed unless roots are explicit in production; or default to a very narrow directory. Document host launch CWD guidance.

---

### F-04 — High

**Title:** Incomplete parent symlink/junction resolution in `PathPolicy`  

**Location:** `PathPolicy.ResolveExistingChain` / `ResolveLinkTarget`  

**Evidence:** When the full path exists as a file, only the **leaf** is passed to `File.ResolveLinkTarget`. Parent reparse points are never walked. `File.Exists` follows parents; `ResolveLinkTarget` on a normal file returns null, so Normalize keeps the lexical under-root path. Reproduced on this host: `trusted/link → ../outside`, file `trusted/link/secret.cs` exists and is not itself a symlink → lexical `IsUnderRoot` true while `realpath` is outside. Non-existent leaves *do* resolve parents (inconsistent).  

**Impact:** `TrustedRoots.Contains` can accept paths whose real I/O target is outside the root. Affects `workspace_open`, XAML path gates, and Workspace Edit preview/apply. Combined with `File.WriteAllText` (follows links) this becomes an **out-of-root write**.  

**Recommended fix:** Canonicalize every path component (realpath / `GetFinalPathNameByHandle` equivalent), then prefix-check. Add tests for parent-dir symlink + junction with an existing leaf.

---

### F-05 — High

**Title:** Fail-open when link resolution throws  

**Location:** `PathPolicy.ResolveLinkTarget` (`IOException` / `UnauthorizedAccessException` → null)  

**Evidence:** Comment: fall back to unresolved path when the link cannot be followed; caller uses unresolved `candidate`.  

**Impact:** A reparse point that cannot be resolved at check time can still pass lexical `IsUnderRoot`, while later I/O follows the link or fails oddly. Fail-open vs fail-closed.  

**Recommended fix:** If a reparse point is detected but the final target cannot be resolved, fail closed (`Contains` → false / explicit policy error).

---

### F-06 — High

**Title:** Workspace Edit apply TOCTOU; write path not bound to verified final path  

**Location:** `WorkspaceEdit.Apply`; `WorkspaceHost.WriteDeclaredPaths` / rollback  

**Evidence:** Apply re-checks `Contains` + `PathExists`, then calls the writer. Writer does **not** call PathPolicy/TrustedRoots; after OldText match it `File.WriteAllText(document.Path, ...)`. Rollback uses the same API. Between check and write, a leaf (or parent dir) can become a symlink to an outside file.  

**Impact:** Concurrent local FS mutation (or hostile workspace content) can turn an approved preview into an outside write.  

**Recommended fix:** Re-run PathPolicy on the **final** path immediately before write (or write via an already-opened handle after `GetFinalPathNameByHandle`). Enforce TrustedRoots inside the writer (F-07).

---

### F-07 — Medium

**Title:** Writer seam skips PathPolicy (defense-in-depth gap)  

**Location:** `IWorkspaceEditWriter.WriteDeclaredPaths`; `WorkspaceHost.WriteDeclaredPaths`  

**Evidence:** Production adapter never sees `TrustedRoots`. Policy is centralized in `WorkspaceEdit` only. Tests can call the host writer directly.  

**Impact:** Future callers or regressions can write without a root check.  

**Recommended fix:** Hard-gate TrustedRoots (or final-path check) inside `WriteDeclaredPaths`; store normalized final paths on the preview.

---

### F-08 — Medium / High (read)

**Title:** F# snapshot enumeration follows directory symlinks without TrustedRoots  

**Location:** `WorkspaceSession.ReadFSharpDocuments` (`Directory.EnumerateFiles(..., AllDirectories)` + `File.ReadAllText`)  

**Evidence:** Enumerates `*.fs` under project dirs with no TrustedRoots filter; directory symlinks are followed by default. `CaptureFSharp` runs on every ready session construction (F-09).  

**Impact:** Opening an in-root project can pull out-of-root `.fs` content into the F# snapshot consumed by tools.  

**Recommended fix:** Skip symlink directories / require `TrustedRoots.Contains` before read; do not recurse into reparse points.

---

### F-09 — High (correctness)

**Title:** F# text re-captured from disk every ready session (not frozen with Roslyn Epoch)  

**Location:** `WorkspaceSession` ctor → `CaptureFSharp`  

**Evidence:** Each ready tool call builds a new session; ctor always captures F# from disk. Roslyn `Solution` is a frozen reference; F# text is live disk.  

**Impact:** Same `Epoch` can pair a stale Roslyn snapshot with newer F# text (debounce lag), breaking ADR-0002 “one request one snapshot”; large trees pay disk I/O on every query.  

**Recommended fix:** Freeze F# text when Epoch advances (host-owned snapshot), share across sessions; do not re-walk disk per request.

---

### F-10 — Medium

**Title:** `workspace_open` splits policy path vs open path (TOCTOU)  

**Location:** `WorkspaceTools.WorkspaceOpen`  

**Evidence:** `TrustedRoots.Contains(path)` (symlink-aware Normalize), then separate `Path.GetFullPath(path)` + `File.Exists`, then `BeginOpen(fullPath)` — no second `Contains(fullPath)` and no Normalize on the opened string.  

**Impact:** Symlink retarget between check and open; policy decision and load path can disagree on link resolution.  

**Recommended fix:** Normalize once, `Contains` that result, open the same normalized string.

---

### F-11 — Medium

**Title:** Document index / OldText match use `GetFullPath` only  

**Location:** loaded-solution path index; `WriteDeclaredPaths` snapshot match  

**Evidence:** Workspace identity is lexical. Paths that are under-root lexically but outside via parents still index, pass OldText (content is the outside file), and get written. Always-ignore-case maps are wrong on case-sensitive Unix volumes (wrong document / missed update).  

**Impact:** Amplifies F-04/F-06; case collisions on Linux.  

**Recommended fix:** Index/match with the same final-path canonicalization as PathPolicy; use Ordinal on non-Windows.

---

### F-12 — Medium

**Title:** Watcher / drift I/O follows loaded graph, not roots  

**Location:** `WorkspaceHost` watcher start + `CheckDrift`  

**Evidence:** Watch directories derive from tracked document paths (and opened solution dir), not filtered by TrustedRoots. Drift repair reads disk text for mismatches.  

**Impact:** If F-02 admits out-of-root projects, the host watches and may ingest those files into the live snapshot.  

**Recommended fix:** Filter watch/repair paths with `TrustedRoots.Contains`.

---

### F-13 — Medium

**Title:** No authn/authz beyond local stdio + path policy  

**Location:** `ServerHost` (`WithStdioServerTransport`)  

**Evidence:** No tokens, principals, or per-tool ACLs; only TrustedRoots + tool-surface allowlist.  

**Impact:** Anyone who can speak MCP to the process can call all tools (including `workspace_open` and apply). Correct for single-user IDE hosts; unsafe if exposed beyond that peer.  

**Recommended fix:** Document “local peer only”; refuse non-stdio/network bindings unless authenticated.

---

### F-14 — Medium

**Title:** Audit logs path metadata; tool responses return source/generated text (partly by design)  

**Location:** `LoggerAuditLogger`; `AuditOptions`; `project_list_generated_sources`; Workspace Edit DTOs  

**Evidence:** Audit defaults on (`DOTNET_MCP_AUDIT`); logs tool name + path (including denials). Contract: no source body in audit (tested). Preview/generated-source tools return full text by design.  

**Impact:** stderr audit can leak sensitive pathnames. MCP clients necessarily see source once a workspace is open — ADR-0004 treats that as untrusted data, not a secret boundary.  

**Recommended fix:** Optional hash/redact paths in audit; keep “no source in audit” tests.

---

### F-15 — Medium

**Title:** Source-generator attribution can mis-bind on identical content  

**Location:** `GeneratorDriverRunner.MatchTree`  

**Evidence:** After `ReferenceEquals`, fallback is first `Ordinal` content match across generators. Comments admit HintName alone is unsafe.  

**Impact:** Two generators emitting identical text → wrong generator identity on `symbol_attribution` / goto-definition Origin.  

**Recommended fix:** Prefer driver tree identity / stable generator-run pairing; treat multi-hit content match as ambiguity error.

---

### F-16 — Medium

**Title:** XAML (and unscoped resolve) can attach the wrong project's SymbolHandle  

**Location:** `XamlDocumentService` — multiple `ResolveByNameAsync(..., projectId: null)` call sites (~L48, L87, L171, L517)  

**Evidence:** `x:Class` / binding types resolve without scoping to the project that owns the XAML document. Adapters prefer a Roslyn “source-defining” hit, then fallbacks.  

**Impact:** Duplicate type names across projects/TFMs → wrong handle; later edits/navigation target the wrong project.  

**Recommended fix:** Scope resolve to the owning project (or AdditionalDocument’s project); return ambiguity errors when ambiguous.

---

### F-17 — Medium

**Title:** `symbol_attribution` is Roslyn-only (leaky facade)  

**Location:** `SymbolTools.SymbolAttribution` → `_roslyn.GetAttributionAsync`  

**Evidence:** Other symbol tools go through `LanguageAdapters`; attribution bypasses adapters. CONTEXT: F# generator attribution out of scope, but the tool description does not say non-Roslyn is unsupported.  

**Impact:** `fsharp:` handles get Roslyn invalid-handle/language errors; API looks language-uniform but isn’t.  

**Recommended fix:** Dispatch via adapters with explicit `AttributionNotSupported` for F#; update tool description.

---

### F-18 — Medium

**Title:** Apply holds locks across disk I/O; nests with WorkspaceEdit lock  

**Location:** `WorkspaceEdit.Apply`; `WorkspaceHost.WriteDeclaredPaths`  

**Evidence:** Apply stays locked while calling the writer; writer locks for Read/WriteAllText, backfill, Epoch advance.  

**Impact:** Long applies block status, FSW, drift, and other applies; lock order is safe today only because Host never calls Edit under its gate.  

**Recommended fix:** Validate under locks, copy plan, release, write, re-acquire for backfill/epoch; or single-flight writer.

---

### F-19 — Medium

**Title:** Preview store: TTL checked on apply only; expired entries not swept  

**Location:** `WorkspaceEdit`  

**Evidence:** Expiry checked in `Apply`; store cleared on writer `Generation` change; no timer/lazy purge of expired items. Previews hold full OldText/NewText.  

**Impact:** Long-lived process + many large previews → memory growth until next `workspace_open` generation bump.  

**Recommended fix:** Cap store size; purge expired on Preview/Apply.

---

### F-20 — Medium

**Title:** Load `CancellationTokenSource` cancel-without-dispose  

**Location:** `WorkspaceHost` cancel inflight/warm; `DisposeAsync`  

**Evidence:** Comments say cancel but do not dispose because inflight load holds Token; replaced warm CTS is not disposed either.  

**Impact:** CTS/native handle growth across repeated opens/cancels.  

**Recommended fix:** Dispose the CTS after the load task completes (`finally`); dispose warm CTS on replace.

---

### F-21 — Medium

**Title:** FSW has no `Error` / buffer-overflow handling  

**Location:** production `FileSystemWatcher` wrapper  

**Evidence:** Subscribes Changed/Created/Deleted/Renamed only; no `Error` handler. Start failures are swallowed on watcher startup.  

**Impact:** Silent missed updates until `workspace_check_drift`; Epoch can lag disk.  

**Recommended fix:** Handle `Error`, recreate watchers, surface a warning on `workspace_status`.

---

### F-22 — Medium

**Title:** `WriteSuppression` uses `OrdinalIgnoreCase` on all OSes  

**Location:** `WriteSuppression`  

**Evidence:** `HashSet` / `Distinct` with `StringComparer.OrdinalIgnoreCase` while Linux paths are case-sensitive. Normalize is `GetFullPath` only.  

**Impact:** Suppressing `/proj/Foo.cs` can also suppress `/proj/foo.cs` (distinct files), dropping real FSW updates.  

**Recommended fix:** Platform path comparer; optionally suppress both lexical and final paths.

---

### F-23 — Medium

**Title:** ADR Windows checklist (UNC, `\\?\`, junction) vs thin tests  

**Location:** ADR-0004 consequences; `PathPolicyTests` / `PathPolicySeamTests` / Workspace Edit tests  

**Evidence:** Tests cover child/sibling, `..` traversal for open, outside-root preview refusal. **No** symlink, junction, UNC, `\\?\`, ADS, or case-sensitivity matrix; **no** out-of-root `.slnf` project tests.  

**Impact:** Regressions for F-02/F-04/F-06 will not be caught.  

**Recommended fix:** Final-path normalization including `\\?\`; platform tests for junctions, UNC, parent symlinks, out-of-root `.slnf`.

---

### F-24 — Low

**Title:** Hard links / same-inode writes not modeled  

**Location:** PathPolicy only resolves symlinks/junctions via `ResolveLinkTarget`  

**Evidence:** Hard links are not links; a hard link under the root to an inode also linked outside is “under root” by path.  

**Impact:** If untrusted content can place hard links in the workspace, apply overwrites that inode.  

**Recommended fix:** Document as residual risk; optionally compare device/inode (Unix) / refuse unexpected link counts.

---

### F-25 — Low

**Title:** `CompilationLru` thundering herd; `FindHitCache` unbounded within an Epoch  

**Location:** `CompilationLru`; `FindHitCache`  

**Evidence:** Miss path compiles outside the lock; concurrent misses both compile. Find cache clears only on epoch advance.  

**Impact:** CPU/memory spikes under parallel warm + query; many find-refs retain full hit lists for the Epoch.  

**Recommended fix:** Per-`ProjectId` singleflight; LRU/cap per epoch.

---

### F-26 — Low

**Title:** Confusing APIs / DTO gaps  

**Location:** symbol tool descriptions; XAML DTOs omit `InteropKind`; tools pass `softBudget: null` (`SoftBudgetOptions` is DI-registered)  

**Impact:** Models mis-call tools; COM interop axis dropped on XAML resolves; soft-budget env knobs appear unused at the tool layer (adapter still falls back to injected defaults).  

**Recommended fix:** Align descriptions; map `InteropKind` consistently; thread `SoftBudgetOptions` explicitly from tools.

---

### F-27 — Info (positive)

**Title:** No classic command-injection / HTTP SSRF tool surface in product code  

**Location:** whole `src/`; `ToolSurfaceGuardTests`  

**Evidence:** No product `Process.Start` / `HttpClient` / `WebRequest`. Guard forbids shell/http/fetch/download/network fragments.  

**Impact:** No first-party shell/command-injection or app-level HTTP SSRF tool. Residual network/process activity would only be inside MSBuild/SDK/analyzers after open (F-01).

---

### F-28 — Info (positive)

**Title:** XAML/XML XXE hardened; `.slnf` is typed JSON  

**Location:** `XamlDocumentService` reader factory (~L1024–1031); `SlnfParser`  

**Evidence:** `DtdProcessing = DtdProcessing.Prohibit`, `XmlResolver = null`. XAML text comes from the workspace snapshot (after path policy). `.slnf` uses System.Text.Json POCOs (no `XmlDocument` / DTD).  

**Impact:** XXE/SSRF via in-product XAML/XML parsers is mitigated. Remaining `.slnf` risk is path resolution (F-02), not deserialization RCE.

---

### F-29 — Info (positive)

**Title:** Tool-surface guards and handwritten-diff gate are effective  

**Location:** `ToolSurfaceGuardTests`; `HandwrittenDocumentDiff`  

**Evidence:** Exact 31-name allowlist; forbids generic write/shell/network; `HandwrittenDocumentDiff` excludes Origin=SourceGenerator documents from write slices; Edit kind/epoch/TTL/OldText checks reduce confused-deputy apply.  

**Impact:** Good guardrail against accidental generic write tools and direct writes to generated files; does **not** prove TrustedRoots depth (F-02–F-06).

---

## Trust-boundary summary

| Input class | Validation | Gap |
|-------------|------------|-----|
| Path tools (`workspace_open`, XAML `path`) | `TrustedRoots.Contains` → PathPolicy | Entry only; not transitive loads (F-02); parent links (F-04) |
| Workspace Edit paths | Contains at preview **and** apply; target must exist; OldText match | Writer has no policy; TOCTOU (F-06/F-07) |
| Symbol handles | Checksummed opaque format | Not a filesystem boundary |
| Pagination `limit` | Clamped | — |
| Cursors | Base64 JSON + epoch binding | Not authz |
| Freeform names / IDs | Typed MCP params | No shell → no classic injection scrubbing needed |

**Trust boundary:** MCP client process ↔ stdio server; filesystem gated by trusted roots; **execution** gated only by “don’t open untrusted repos.”

---

## Security-critical test gaps

- No parent-directory symlink / junction cases (F-04).  
- No link-resolution fail-closed cases (F-05).  
- No concurrent apply vs external write / post-check link swap (F-06).  
- No out-of-root `.slnf` project / ProjectReference cases (F-02).  
- No UNC / `\\?\` normalization cases (F-23).  
- Allowlist tests are strong for **tool names**, weak for **path-policy depth**.

---

## Bottom line

The MCP façade is disciplined (envelope + allowlist + kind-tagged Workspace Edit + open-means-execute disclosure + XXE hardening + no first-party shell/HTTP tools). The real residual weaknesses are **shallow TrustedRoots** (entry args only), **CWD default**, **parent reparse-point normalization gap**, **check-then-write without final-path binding**, and **F# snapshot not frozen with the Roslyn Epoch**. Open-means-execute remains the dominant product hazard and is amplified by F-02.

This report is a read-only deliverable: no exploit PoCs, no attack payloads, no production code fixes.
