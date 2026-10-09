# Model File Import — Manual Verification Checklist

The `asset_gen` group now contains only `import_model_file`, which copies a local model
file into a licensed Unity Editor project and imports it, so it **cannot be covered
headlessly**. Run this checklist by hand in an interactive Editor before shipping.

## Prerequisites

- [ ] A licensed Unity Editor with the package installed and the bridge connected.
- [ ] Enable the group: `manage_tools` → enable `asset_gen` (it is off by default).

## Local files (`import_model_file`)

- [ ] `import_model_file(source_path="/path/to/model.fbx")` imports under `Assets/`.
- [ ] Repeat with `.obj` and a `.zip`.
- [ ] Confirm the **path-traversal guard** holds for a `.zip` (no files written outside the target dir).
- [ ] Install **glTFast** from the **Deps** tab, then import a `.glb` and confirm there is no missing-importer error.
