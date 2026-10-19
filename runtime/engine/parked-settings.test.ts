import { expect, test } from "bun:test";
import { setImmediate } from "node:timers/promises";
import { Settings, settings } from "./coding-agent/src/config/settings";
import { withActiveSettings } from "./coding-agent/src/config/settings-context";
import { cfgToolsApprovalMode } from "./coding-agent/src/tools/settings";

// Parking retains the parent's settings plus the child's plain overlay recipe,
// not the child itself. Accessing the ambient proxy must preserve that property.
function captureParkedSettings(parent: Settings) {
	const overlay = parent.overlay({ "tools.approvalMode": "always-ask" });
	withActiveSettings(overlay, () => {
		const getCwd = settings.getCwd;
		expect(settings.getCwd).toBe(getCwd);
		expect(getCwd()).toBe(overlay.getCwd());
	});
	return { ref: new WeakRef(overlay), layers: overlay.overlayLayers() };
}

test("the scoped settings method cache does not retain a parked overlay", async () => {
	const parent = Settings.isolated();
	const parked = captureParkedSettings(parent);
	let collected = false;
	for (let turn = 0; turn < 50 && !collected; turn++) {
		// GC and WeakRef job lifetime are real runtime behavior, not timer behavior.
		await setImmediate();
		Bun.gc(true);
		collected = parked.ref.deref() === undefined;
	}
	expect(collected).toBe(true);
	expect(cfgToolsApprovalMode.get(parent.restoreOverlay(parked.layers))).toBe("always-ask");
}, 10_000);
