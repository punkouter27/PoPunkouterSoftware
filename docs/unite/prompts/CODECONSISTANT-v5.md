# CODECONSISTANT v5 — full prompt

v4 verbatim, with **Profile C** replaced by the fix pass in `PROMPT-ProfileC.md`. Nothing else changed.
Paste this whole block at the start of a session.

---

## CODECONSISTANT v5

**Goal:** Audit and fix the repos in `_CODEMATT` (skip MultiAppE2ETests). Publish one HTML report: repo × rule matrix (before → after, with evidence) plus the top 20 ideas to simplify and unify. Republish after each repo.

**Status:** PASS, FAIL, N/A or NOT VERIFIED, before and after. Evidence: `file:line`, a command and its output, a run URL, or a screenshot you opened. PASS needs a test run in this session. Flaky = FAIL; three green full runs in a row prove it isn't. Signed-in email passes only through a test sign-in that checks the header.

**Hard rules**

- No shared code between repos. Azure is shared only through PoShared.

- `master` is the only branch. Read `git log -40` and `gh run list`. If an owner commits conflicts with a rule, apply the rule and list the commit's sha as an exception.

- Live sites are on free tiers (F1 quota). Never run test suites against them. Live checks: at most 6 page loads before and after (home, `/diag`, one deep page), plus axe and X-Commit. Everything else runs locally.

**All repos**

- Title top-left, email top-right (OAuth apps only), `vYYMMDD` bottom-right; on phones the stamp comes after the content. Nothing overlaps or scrolls sideways at 390 px.

- `/diag` probes every service live, using cheap, non-billable calls with cached results. Services off on purpose show "Off" and don't count toward the banner.

- One workflow file: format, build, test, deploy, confirm X-Commit.

- 0 warnings. Every test tier passes. Docker and browser tiers run for real.

**Profile A (Blazor)**

- `.slnx`, `global.json`, `.editorconfig`, `Directory.Build.props` with Nullable on and warnings as errors.

- Radzen only (load it before the app's CSS and use a plain class to keep the look). Exceptions: resumable file inputs, audio/video, read-only tables.

- One layout, a NotFound page, loading/empty/error states. Navigation hidden when signed out.

- FluentValidation with inline errors. Validators registered by type. No `alert()` or `confirm()`.

- Feature-module endpoints, Serilog, health checks.

- Sign-in: none for PoMiniGames (initials), PoMode (initials), PoPunkouterSoftware and PoRobotTrain; GitHub for PoRepoLineTracker; Entra for the rest. The sign-in page shows the title and one button.

Profile B (MiniGames)

**PoMiniGames:** Menu → intro → game → end card, with no scrolling at 1366×768 or 375×667. Minimal HUD, TAB dev overlay, one public lobby, WebGPU with WebGL fallback. Show FPS and WebGPU/WebGL on top center of nav bar

**Profile C (RL / MuJoCo) — fix, not just audit**

Authoritative rules: `global/UNITE/RULES.md` (28 rules). This profile is the *apply* pass for that file. It does not restate the rules; it runs them and records the result. If this text and RULES.md ever disagree, RULES.md wins and this text is the bug.

*Scope gate first — classify every repo before touching it.* The class is a column in the matrix.

| Class | Repos | What Profile C does |
|---|---|---|
| **C0 full** | `PoRobotTrain`, `PoBasketball`, `PoRaceUnreal`, `PoSlapUnreal` | all six parts of RULES.md §5 |
| **C1 partial** | `PoBox`, `PoHillKing`, `PoOlympic` | Parts 1–2 only where a contract resolves for that repo's creature; otherwise record a **contract gap** and stop that repo |
| **C2 consumer-only** | `PoFootball`, `PoSoccer`, `PoSumo` (Unity ML-Agents) | never attempt Part 2; N/A on interface, parity and portability; only the contract-driven loader if it is a small change |
| **N/A** | every other repo | Profile C does not apply. Record one N/A row and move on |

An N/A row is not a FAIL. Never "fix" an N/A row by inventing a contract or hand-writing interface constants — that is R28. A C2 repo's own trainer output is always `legacy`; portable brains for that game must come from a MuJoCo trainer.

*The fix, per C0 repo* — RULES.md §5, in order, no reordering, no skipping:

1. **Adopt the contract, zero behaviour change.** `contracts.lock.json`; one loader module is the only source of `n_obs`, `n_action`, joint order, roles, gains, `action_scale`, `control_dt`, `decimation`, `timestep`, solver `option`; generated constants with a `--check` mode. If existing tests move, this part is wrong — revert it and report.
2. **Enforce the interface for new training only.** 131 role-ordered floats with `role_mask` and `morphology_onehot`; 28-wide action with the clip rule; plain MLP, no normalisation in the graph; sidecar on every `.onnx`; **the export entrypoint aborts on a layout-hash mismatch** (R11).
3. **The runtime refuses, not degrades.** Sidecar vs contract vs lock before a policy runs; on mismatch do not load, log the three hashes, surface "policy incompatible" where the app already shows status; a test that corrupts a sidecar and expects refusal. Never reshape inputs to make an old brain fit.
4. **Physics and world.** MuJoCo steps; the engine renders and observes. One MuJoCo version per contract. Record the contact block actually used. Randomisation on and recorded for anything meant to transfer.
5. **Contain the legacy.** `agents/registry.json` listing every existing `.onnx` as `legacy` with its interface hash or `null`; a test that fails on a new `.onnx` without a sidecar; the training entrypoint prints the contract and interface hash it is training against. Do not delete, do not promote.
6. **Report** per the §5 Part 6 shape: rule → before → after → evidence, plus grandfathered, not-done and contract gaps.

For **C1** repos: run Parts 1–2, stop at the first missing contract, and submit the gap list as the finding. For **C2** repos: run only step 3's loader check if it is a small change; everything else is N/A.

*Profile C also keeps these existing checks* — unchanged, still PASS/FAIL:

- Unity: `Assets/_Project/{Agents,Core,Data,Environments,RL_Models,Scenes,UI}` + `/Training`, moved with `git mv` plus `.meta`. Tests in batchmode with `-force-d3d11`, `CI=true`, `UNITY_MCP_KEEP_CONNECTED=false`.

- Unreal: `/Game/Project/{Agents,Core,DataAssets,Environments,RL_Policies,UI}`, moved only through UnrealEditor-Cmd Python rename plus redirector fixup. Verify 0 redirectors and 0 load failures, or leave the project untouched. UI tests must render (no `-nullrhi`).

- Physics: gravity `[0,0,-9.81]`, joint limits, PD actuators, no adjacent-bone collision, and the full contact block — `friction` (3-vector), `solref`, `solimp`, `priority`, `condim` — plus the solver `cone`. Not "friction" alone. A drawn-only gaze is allowed where there's no neck joint.

- Each penalty term is tracked and tested. No training runs. BC/AMP only where mocap exists.

- ScriptableObjects or DataAssets for settings. 16:9 and 9:16. Title and version once, outside the canvas. Confirm in the browser console that the policy runs.

- TAB overlay (FPS, reward, latency, stage) with an honestly labelled benchmark. 30 s attract mode whose timer ignores frames over 0.25 s.

- **The creature comparison is generated, not written.** The root `_CODEMATT` HTML is built from `global/UNITE/creatures/*/contract.json`: game, creature, joints, actuators, sensors, roles mapped, `n_obs`/`n_action`, hardware yes/no, and the `interface_sha256` each row came from. A hand-edited row is FAIL; a creature with no contract is a contract gap, not a blank cell.

*New matrix rows.* Add these alongside the existing ones; the first four are the only ones that prove the concept actually landed.

| Row | PASS means |
|---|---|
| C-scope | the repo is classified C0 / C1 / C2 / N/A, with the reason |
| C-contract | `contracts.lock.json` exists and `build_creatures.py --check` → `OK` |
| C-loader | one module is the only source of interface values — cite it, and cite a removed duplicate |
| C-guard | the export entrypoint aborts on a layout-hash mismatch — show the abort output |
| C-sidecar | a current sidecar exists with all three hashes; the interface hash equals the contract's |
| C-refusal | the corrupt-sidecar test passes |
| C-parity | parity output with the max deviation and the tolerance |
| C-legacy | `agents/registry.json` lists every existing `.onnx`, none promoted |
| C-leak | a new `.onnx` without a sidecar fails CI |
| C-world | the contact block and `cone` are recorded; randomisation is on |
| C-creature-page | the root comparison HTML was generated from contracts, not typed |
| C-portable | `check_creatures.py` → `OK` for each of the repo's creatures |

*Evidence.* Unchanged from the audit's standard, with three additions: RULES.md rule numbers cited per row, the three hashes pasted rather than summarised, and the abort/refusal output pasted rather than described. N/A needs a reason, not just the label.

*Profile C never:*

- invents a contract, or hand-writes an interface constant to unblock itself (R28);
- reorders, pads or rescales observations so an old brain loads (R13/R14);
- migrates, retrains or promotes an existing checkpoint (R22);
- bakes normalisation into a graph (R9);
- upgrades a MuJoCo pin to make a test pass (R17);
- forces two apps onto identical worlds to claim compatibility;
- claims sim-to-real fidelity without `sim_real_diff`;
- edits `global/UNITE` from inside a repo — report the gap instead (R27).

*Running Profile C.* Scope-gate the whole fleet first and write the C0/C1/C2/N/A column before touching a repo — that is what keeps the matrix honest and stops N/A rows from looking like failures. Then run C0 in this order: `PoRobotTrain` (hardware path), `PoBasketball` (second creature), `PoRaceUnreal` (in-engine rebuild path), `PoSlapUnreal`. One repo per pass, republish after each. After every pass, if `interface_sha256` moved, that pass is a FAIL: the whole design depends on that value never changing.

**Per repo:** audit before, fix, run local checks, rebase, commit only your files, push (approved, except PoRobotTrain: stays local), watch the deploy, verify live, record after. Leave a clean tree, no stashes, and nothing running that you started. For Profile C repos, "fix" means the six parts above, and "record after" means the C-rows with pasted output.
