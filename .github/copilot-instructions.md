# Copilot Instructions

## Project Guidelines
- For rt4k_pi, preserve ANSI colors in journalctl output. Do not disable console color formatting solely because stdout is redirected.
- In rt4k_pi, suppress IDE0130 repository-wide: responsibility folders intentionally do not dictate namespaces.
- For rt4k_pi's web UI, favor simple, nontechnical instructions and collapsible warnings; avoid confusing manual recovery controls.
- On rt4k_pi's Settings page, place new setting controls inside the existing Settings table with matching row styling and spacing, rather than introducing standalone sections unnecessarily.
- For RT4K firmware updates, leverage the device's built-in verified atomic put and fwup validation. Use streaming put and report bytes sent without requiring per-frame acknowledgments. Do not issue sha256 serial commands or add duplicate copy-and-move staging; do not clean up partial transfers because the device owns its transfer temporary files. Interrupted operations should be canceled and cleaned up automatically, never resumed. Do not expose a manual recovery/resume action; preserve safety checks before deleting staged or boot files.
- In rt4k_pi, the firmware Refresh button must fetch fresh available releases rather than reuse the firmware catalog cache.

## Documentation Guidelines
- Keep README.md focused on features, setup/update instructions, and practical limitations (such as file size limits); omit implementation details, internal architecture, protocol mechanics, and developer test instructions.