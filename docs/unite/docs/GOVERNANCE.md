# UNITE governance

Rules that keep one contract from becoming five again.

---

## 1. Versioning

| Thing | Scheme | Breaks when |
|---|---|---|
| `pocontract.schema.json` | integer `contract_version` | a required property is added, removed or retyped |
| the generator | semver `pocontract 1.4.2` | any output changes for the same input |
| the canonical role list | integer `roles_version` | a role is added, renamed or reordered |
| an emitted contract | the SHA-256 triple | any of the three hashes changes |

Rules:

- **Additive changes are minor.** A new optional property is `1.x`; old readers ignore it.
- **Any reorder of `roles.canonical_order` is major and requires a retrain.** Reordering
  silently changes the meaning of every weight in every policy. There is no migration
  for this; there is only a new `roles_version` and a `legacy_interfaces` entry.
- **A text-only edit to a slot's `rule` changes `interface_sha256`.** That is deliberate:
  the prose is part of the contract, because a runtime may implement the prose.
- **Never reuse a version number.** A withdrawn contract is retired, not overwritten.

---

## 2. Vendoring, not syncing

The Po Game Standard forbids one repo generating into or checking files of another. UNITE
is compatible with that:

```
<repo>/third_party/pocontract/
    VERSION                     <- "1.4.2"
    pocontract/                 <- the tool, byte-identical to the release artifact
<repo>/contracts/<creature>.json    <- emitted in that repo, committed there
<repo>/tests/test_contract.py       <- asserts --check is clean + version allowlisted
```

- The release artifact is versioned (Azure Artifacts, a private PyPI, or a zipped
  `pocontract-1.4.2.zip` with a published SHA-256 — whichever is cheapest today).
- A repo upgrades by replacing the vendored folder and re-running `--check`. If the
  emitted contract changes, that is visible in the diff, with the hash change right there.
- No file is copied from repo A into repo B, ever.

---

## 3. Adding a creature

1. Author the MJCF (or reuse an existing one) in that repo, unchanged in structure.
2. Add an entry to `REGISTRY` in `tools/build_creatures.py`: source path, `kind`, `root_body`,
   and a one-line note. Add `hardware=True` + `hardware_yaml` only for a creature that is also a
   real robot.
3. Run `tools/build_creatures.py <id>`. It splits the source into a portable `creature.xml`
   (no option/floor/light, no `meshdir`, `angle="radian"`, meshes vendored) plus `scene.xml`,
   auto-maps every joint to a canonical role, and writes `role_map.json` and `contract.json`.
4. Read `role_map.json`. Fix anything the auto-mapper got wrong — that file is the input, not a
   by-product. Rerun the builder.
5. Run `tools/check_creatures.py`. It must be `OK`: identical joint tables standalone vs. a foreign
   `<include>`, and no joint axis, range or body mass changed by the split.
6. Run `tools/validate.py`. It must be `OK`.

If a creature genuinely needs a joint no canonical role describes, **do not invent a
local role**. Open a `roles_version` bump. That is the whole point of the socket.

---

## 4. Policy sidecar — required fields

Every `.onnx` ships with a sidecar. A runtime that cannot match all of these must refuse
to load it rather than run it approximately.

```json
{
  "policy_name": "...",
  "n_obs": 131,
  "n_action": 28,
  "opset": 17,
  "input": "obs[1,131]",
  "output": "action[1,28] in [-1,1]",
  "interface_sha256": "...",
  "morphology_sha256": "...",
  "physics_sha256": "...",
  "policy_sha256": "...",
  "mujoco": "4.0.0",
  "ort_vs_torch_max_abs_diff": 9.8e-07,
  "training": { "steps": 25067520, "seed": 0, "git": "..." }
}
```

`PoBasketball/shared/policies/*.json` and `PoRobotTrain/RobotControl/models/*/deploy.json`
are both almost this already; converge them rather than inventing a third shape.

---

## 5. Review checklist

Run this on any PR that touches a contract, a joint map, an observation, or an action.

- [ ] `pocontract --check` clean in the repo that changed.
- [ ] Hashes in every affected policy sidecar updated, and every shipped policy either
      re-gated or explicitly marked stale.
- [ ] No reorder of `canonical_order` without a `roles_version` bump and a retrain.
- [ ] `n_obs` / `n_action` derived by the generator, not typed by hand.
- [ ] Golden fixtures regenerated and the parity test still under tolerance.
- [ ] Physics pin unchanged, or a new contract emitted for the new pin.
- [ ] If a joint moved into `held_outside_brain`, its nominal pose and gains are recorded.
- [ ] If the change is meant to be invisible to a policy, `interface_sha256` did **not**
      change — prove it, do not assume it.

---

## 6. Drift detection

One scheduled job, outside every repo, that:

1. checks out each repo at its default branch,
2. reads `third_party/pocontract/VERSION` and every `contracts/*.json`,
3. prints a table of repo → generator version → interface hash per creature.

Drift is then a version mismatch on one line, not a policy that behaves oddly six weeks
later. `PoBasketball`'s `sim/tools/sync_policies_to_unreal.py` already warns on a
`obs_spec` hash mismatch; this generalizes that instinct across repos without syncing
anything.
